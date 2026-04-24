using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class CustomRulesViewModel : ObservableObject
    {
        private readonly SettingsService _settings;
        private readonly XrayService _xray;
        private readonly GeoDataUpdateService _geoUpdate;
        private readonly IDialogService _dialogs;
        private readonly Func<Task>? _reapplyRouting;
        private readonly Func<bool>? _isTunMode;
        private readonly Func<string?>? _getProxyUrl;

        private bool _isEffectiveNow;

        public ObservableCollection<CustomRoutingRule> Rules { get; } = new();

        /// <summary>True iff current RoutingMode is "smart". UI shows a banner when false.</summary>
        public bool IsEffectiveNow
        {
            get => _isEffectiveNow;
            private set
            {
                if (SetProperty(ref _isEffectiveNow, value))
                {
                    OnPropertyChanged(nameof(IsNotEffectiveNow));
                    OnPropertyChanged(nameof(NotEffectiveVisibility));
                }
            }
        }

        public bool IsNotEffectiveNow => !_isEffectiveNow;

        // Direct Visibility binding — avoids converter lookup in Window root.
        public Visibility NotEffectiveVisibility => _isEffectiveNow ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// View is expected to open AddRuleDialog when this fires.
        /// Payload == null → Add new; Payload != null → Edit existing.
        /// After dialog confirms, View calls back into <see cref="AddNewRule"/>
        /// or <see cref="ReplaceRule"/>.
        /// </summary>
        public event EventHandler<CustomRoutingRule?>? ShowAddOrEditDialogRequested;

        /// <summary>View closes the window when this fires.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Returns the XamlRoot of the hosting CustomRulesWindow. Set by the View in its ctor.
        /// Used so dialogs raised from this VM (progress, success/error toasts) render on the
        /// CustomRulesWindow instead of behind it on MainWindow.
        /// </summary>
        public Func<XamlRoot?>? GetXamlRoot { get; set; }

        public CustomRulesViewModel(
            SettingsService settings,
            XrayService xray,
            GeoDataUpdateService geoUpdate,
            IDialogService dialogs,
            Func<Task>? reapplyRouting,
            Func<bool>? isTunMode,
            Func<string?>? getProxyUrl = null)
        {
            _settings       = settings;
            _xray           = xray;
            _geoUpdate      = geoUpdate;
            _dialogs        = dialogs;
            _reapplyRouting = reapplyRouting;
            _isTunMode      = isTunMode;
            _getProxyUrl    = getProxyUrl;
        }

        public async Task LoadAsync()
        {
            var s = await _settings.LoadSettingsAsync();

            Rules.Clear();
            if (s.CustomRules != null)
            {
                foreach (var r in s.CustomRules)
                    Rules.Add(r.Clone());   // deep copy so UI edits don't mutate persisted list
            }

            IsEffectiveNow = s.RoutingMode == "smart";
        }

        // ── Called by View after dialog returns ───────────────────────────────
        public void AddNewRule(CustomRoutingRule rule) => Rules.Add(rule);

        public void ReplaceRule(CustomRoutingRule original, CustomRoutingRule updated)
        {
            var idx = Rules.IndexOf(original);
            if (idx >= 0) Rules[idx] = updated;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        [RelayCommand]
        private void AddRule() => ShowAddOrEditDialogRequested?.Invoke(this, null);

        [RelayCommand]
        private void EditRule(CustomRoutingRule rule) =>
            ShowAddOrEditDialogRequested?.Invoke(this, rule);

        [RelayCommand]
        private void DeleteRule(CustomRoutingRule rule) => Rules.Remove(rule);

        [RelayCommand]
        private async Task Save()
        {
            var s = await _settings.LoadSettingsAsync();
            s.CustomRules = Rules.Count == 0
                ? null
                : Rules.Select(r => r.Clone()).ToList();

            try
            {
                await _settings.SaveSettingsAsync(s);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CustomRules] Failed to persist: {ex.Message}");
            }

            // Rebuild xray config + restart when running in smart mode.
            if (_reapplyRouting != null && _xray.IsRunning && s.RoutingMode == "smart")
            {
                try
                {
                    await _reapplyRouting();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CustomRules] Failed to reapply routing: {ex.Message}");
                }
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

        // ── Update geo data ──────────────────────────────────────────────────

        /// <summary>
        /// Invoked from the View after the user confirms in the refresh button's Flyout.
        /// Shows a modal progress dialog, downloads (or skips if already latest), restarts xray
        /// if something actually changed, then surfaces the result. All dialogs are rooted in
        /// the CustomRulesWindow via <see cref="GetXamlRoot"/>.
        /// </summary>
        [RelayCommand]
        private async Task UpdateGeoData()
        {
            var xamlRoot = GetXamlRoot?.Invoke();

            // Route through xray's local SOCKS5 port when it's running — in mainland China
            // GitHub's releases CDN is often unreachable or painfully slow, and the user
            // already has a working tunnel up. Null = direct connection.
            var proxyUrl = _getProxyUrl?.Invoke();

            GeoDataUpdateService.UpdateResult result = default;

            try
            {
                await _dialogs.ShowProgressDialogAsync(
                    "正在更新路由数据",
                    async (progress, ct) => result = await _geoUpdate.UpdateAsync(progress, proxyUrl, ct),
                    xamlRoot);
            }
            catch (OperationCanceledException ex) when (ex.GetType() == typeof(OperationCanceledException))
            {
                // DialogService throws exactly `new OperationCanceledException()` for user cancel.
                // Any subclass (e.g. TaskCanceledException from HttpClient.Timeout) falls through
                // to the generic Exception catch below so the failure is surfaced, not swallowed.
                return;
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync("更新失败", ex.Message, xamlRoot);
                return;
            }

            // Everything was already current — don't bother restarting xray.
            if (!result.AnyUpdated)
            {
                await _dialogs.ShowErrorAsync(
                    "已是最新",
                    "geoip.dat 和 geosite.dat 都已是最新版本，无需下载。",
                    xamlRoot);
                return;
            }

            // At least one file changed — decide whether to reload xray.
            string message;
            if (_xray.IsRunning)
            {
                if (_isTunMode?.Invoke() == true)
                {
                    message = "已更新。TUN 模式下请手动重启以生效。";
                }
                else if (_reapplyRouting != null)
                {
                    try
                    {
                        await _reapplyRouting();
                        message = "已更新并重新加载 xray。";
                    }
                    catch (Exception ex)
                    {
                        message = $"已更新数据文件，但重启 xray 失败：{ex.Message}";
                    }
                }
                else
                {
                    message = "已更新。请重启 xray 以生效。";
                }
            }
            else
            {
                message = "已更新。下次启动 xray 时生效。";
            }

            await _dialogs.ShowErrorAsync("更新成功", message, xamlRoot);
        }
    }
}
