using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class CustomRulesViewModel : ObservableObject
    {
        private readonly SettingsService _settings;
        private readonly XrayService _xray;
        private readonly IDialogService _dialogs;
        private readonly Func<Task>? _reapplyRouting;

        public ObservableCollection<CustomRoutingRule> Rules { get; } = new();

        /// <summary>True, iff current RoutingMode is "smart". UI shows a banner when false.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotEffectiveNow))]
        [NotifyPropertyChangedFor(nameof(NotEffectiveVisibility))]
        public partial bool IsEffectiveNow { get; private set; }

        public bool IsNotEffectiveNow => !IsEffectiveNow;

        // Direct Visibility binding — avoids converter lookup in Window root.
        public Visibility NotEffectiveVisibility => IsEffectiveNow ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// View is expected to open AddRuleDialog when this fires.
        /// Payload == null → Add new; Payload != null → Edit existing.
        /// After dialog confirms, View calls back into <see cref="AddNewRule"/>
        /// or <see cref="ReplaceRule"/>.
        /// </summary>
        public event EventHandler<CustomRoutingRule?>? ShowAddOrEditDialogRequested;

        /// <summary>View closes the window when this fires.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>Raised after settings.json is opened in an external editor.</summary>
        public event EventHandler? AdvancedEditorOpened;

        /// <summary>
        /// Returns the XamlRoot of the hosting CustomRulesWindow. Set by the View in its ctor.
        /// Used so error dialogs raised from this VM render on the CustomRulesWindow instead
        /// of behind it on MainWindow.
        /// </summary>
        public Func<XamlRoot?>? GetXamlRoot { get; set; }

        public CustomRulesViewModel(
            SettingsService settings,
            XrayService xray,
            IDialogService dialogs,
            Func<Task>? reapplyRouting)
        {
            _settings       = settings;
            _xray           = xray;
            _dialogs        = dialogs;
            _reapplyRouting = reapplyRouting;
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

        /// <summary>
        /// Drop the SettingsService cache and reload Rules from disk. Called by the window
        /// when it regains focus, so externally edited customRules / advancedRouting changes
        /// show up immediately and don't get clobbered by a subsequent Save.
        /// </summary>
        public async Task ReloadFromDiskAsync()
        {
            _settings.InvalidateCache();
            await LoadAsync();
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
            // The user may have edited settings.json externally via Advanced Edit while this
            // window was open. Reload so we only overwrite CustomRules; AdvancedRouting
            // and unrelated fields stay as they are on disk.
            var s = await _settings.ReloadAsync();
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

        /// <summary>
        /// Seed settings.AdvancedRouting with the current default routing template on first
        /// use, then shell-open settings.json so the user can freely edit the full xray
        /// routing object. Cache is invalidated, so the next read picks up the user's edits.
        /// </summary>
        [RelayCommand]
        private async Task OpenAdvancedEditor()
        {
            var xamlRoot = GetXamlRoot?.Invoke();

            try
            {
                var s = await _settings.LoadSettingsAsync();
                if (s.AdvancedRouting is null)
                {
                    s.AdvancedRouting = XrayConfigBuilder.BuildDefaultRoutingTemplate(s);
                    await _settings.SaveSettingsAsync(s);
                }
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync(
                    L.CustomRules_PrepFailedTitle,
                    ex.Message,
                    xamlRoot);
                return;
            }

            try
            {
                _settings.OpenInExternalEditor();
                AdvancedEditorOpened?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync(
                    L.CustomRules_OpenEditorFailedTitle,
                    Loc.Format("CustomRules_OpenEditorFailedMsg", ex.Message),
                    xamlRoot);
            }
        }
    }
}
