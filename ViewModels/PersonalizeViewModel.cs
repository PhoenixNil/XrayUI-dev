using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class PersonalizeViewModel : ObservableObject
    {
        private readonly SettingsService _settings;
        private readonly IDialogService _dialogs;
        private readonly StartupService _startup;

        /// <summary>Exposed so PersonalizeControl code-behind can show the hotkey recorder
        /// dialog — the actual Win32 register/unregister probe stays in code-behind (needs the
        /// Window handle), so this VM doesn't own that flow end-to-end.</summary>
        public IDialogService Dialogs => _dialogs;

        private int _initialLanguageIndex = -1;
        private bool _suppressLanguageRestartHint;
        private int _initialRegionIndex = -1;
        private bool _suppressRegionRestartHint;

        public event EventHandler? CloseRequested;
        public event EventHandler? PresetImported;

        /// <summary>Set by MainViewModel (ControlPanel.IsRunning). A preset/Clash import
        /// reloads the server list from disk, which orphans the live connection's node
        /// reference without touching the running xray process — the caller checks this
        /// before importing and blocks with a message to disconnect first, rather than
        /// silently leaving the UI showing a "running" state with no active node.</summary>
        public Func<bool>? IsProxyRunning { get; set; }

        public PersonalizeViewModel(IDialogService dialogs, SettingsService settings, StartupService startup)
        {
            _dialogs = dialogs;
            _settings = settings;
            _startup = startup;
            ShowLatencyInDetails = true;
            ShowAiUnlockInDetails = true;
            ShowGroupInDetails = true;
        }

        // ── Colors ────────────────────────────────────────────────────────────

        [ObservableProperty]
        public partial Color SsColor { get; set; }

        [ObservableProperty]
        public partial Color VlessColor { get; set; }

        [ObservableProperty]
        public partial Color VmessColor { get; set; }

        [ObservableProperty]
        public partial Color Hysteria2Color { get; set; }

        [ObservableProperty]
        public partial Color FallbackColor { get; set; }

        partial void OnSsColorChanged(Color value)
        {
            ProtocolColorStore.Ss = value;
            ProtocolColorStore.NotifyColorsChanged();
        }

        partial void OnVlessColorChanged(Color value)
        {
            ProtocolColorStore.Vless = value;
            ProtocolColorStore.NotifyColorsChanged();
        }

        partial void OnVmessColorChanged(Color value)
        {
            ProtocolColorStore.Vmess = value;
            ProtocolColorStore.NotifyColorsChanged();
        }

        partial void OnHysteria2ColorChanged(Color value)
        {
            ProtocolColorStore.Hysteria2 = value;
            ProtocolColorStore.NotifyColorsChanged();
        }

        partial void OnFallbackColorChanged(Color value)
        {
            ProtocolColorStore.Fallback = value;
            ProtocolColorStore.NotifyColorsChanged();
        }

        // ── Theme ─────────────────────────────────────────────────────────────
        // Bound TwoWay to CommunityToolkit Segmented.SelectedIndex.
        // 0 = Light, 1 = Dark, 2 = System/Default

        [ObservableProperty]
        public partial int SelectedThemeIndex { get; set; }

        partial void OnSelectedThemeIndexChanged(int value)
        {
            var theme = value switch
            {
                0 => ElementTheme.Light,
                1 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            ThemeHelper.ApplyTheme(theme);
        }

        // ── Backdrop ──────────────────────────────────────────────────────────

        [ObservableProperty]
        public partial int SelectedBackdropIndex { get; set; }

        partial void OnSelectedBackdropIndexChanged(int value) =>
            ThemeHelper.ApplyBackdrop(value == 1 ? "Acrylic" : "Mica");

        // ── Language ──────────────────────────────────────────────────────────

        /// <summary>Bound to the language ComboBox's ItemsSource — single source of truth
        /// for the dropdown contents. Adding a language is a one-line edit in LanguageHelper.</summary>
        public LanguageInfo[] SupportedLanguages => LanguageHelper.SupportedLanguages;

        [ObservableProperty]
        public partial int SelectedLanguageIndex { get; set; }

        partial void OnSelectedLanguageIndexChanged(int value)
        {
            // Hint visibility tracks divergence from the loaded value, not whether the user
            // touched the dropdown — flipping back to the initial choice clears the hint too.
            if (!_suppressLanguageRestartHint)
                UpdateRestartHint();
        }

        // ── Region (domestic region for smart routing) ─────────────────────────
        // Lives under the Application-language expander. Like language, it only takes effect
        // on the next process start, so it shares the restart hint below.

        /// <summary>Region codes, in the same order as the region ComboBox items in PersonalizeControl.xaml.</summary>
        private static readonly string[] RegionCodes = { "cn", "ru", "ir" };

        [ObservableProperty]
        public partial int SelectedRegionIndex { get; set; }

        partial void OnSelectedRegionIndexChanged(int value)
        {
            if (!_suppressRegionRestartHint)
                UpdateRestartHint();
        }

        /// <summary>Selected region code, clamped to a valid entry; persisted to <see cref="AppSettings.RoutingRegion"/>.</summary>
        private string SelectedRegionCode =>
            (uint)SelectedRegionIndex < (uint)RegionCodes.Length ? RegionCodes[SelectedRegionIndex] : RegionCodes[0];

        /// <summary>True when language or region diverges from the loaded baseline — both apply
        /// only after a process restart, so the InfoBar offers one.</summary>
        [ObservableProperty]
        public partial bool ShowRestartHint { get; set; }

        private void UpdateRestartHint()
        {
            var langDiverged   = _initialLanguageIndex >= 0 && SelectedLanguageIndex != _initialLanguageIndex;
            var regionDiverged = _initialRegionIndex   >= 0 && SelectedRegionIndex   != _initialRegionIndex;
            ShowRestartHint = langDiverged || regionDiverged;
        }

        /// <summary>
        /// Persists the restart-gated settings — language and routing region, which only take
        /// effect on the next process start. Returns false when nothing was written, so the caller
        /// does not restart into a process that comes back showing the old values.
        /// </summary>
        public async Task<bool> ApplyPendingChangesAsync()
        {
            var s = await _settings.LoadSettingsAsync();
            if (s.IsFailedLoadFallback)
            {
                await _dialogs.ShowErrorAsync(L.Settings_InvalidTitle, L.Settings_InvalidMsg);
                return false;
            }

            s.Language = LanguageHelper.TagAt(SelectedLanguageIndex);
            s.RoutingRegion = SelectedRegionCode;
            return await _settings.SaveSettingsAsync(s);
        }

        [ObservableProperty]
        public partial bool ShowLatencyInDetails { get; set; }

        partial void OnShowLatencyInDetailsChanged(bool value) => UpdateDisplaySettingsUnsavedHint();

        [ObservableProperty]
        public partial bool ShowAiUnlockInDetails { get; set; }

        partial void OnShowAiUnlockInDetailsChanged(bool value) => UpdateDisplaySettingsUnsavedHint();

        [ObservableProperty]
        public partial bool ShowGroupInDetails { get; set; }

        partial void OnShowGroupInDetailsChanged(bool value) => UpdateDisplaySettingsUnsavedHint();

        [ObservableProperty]
        public partial bool OpenServerFilterPanelOnStartup { get; set; }

        partial void OnOpenServerFilterPanelOnStartupChanged(bool value) => UpdateDisplaySettingsUnsavedHint();

        /// <summary>True once any of the display toggles above diverges from the
        /// last-loaded/last-saved baseline. They all apply live immediately (see
        /// MainViewModel's PropertyChanged wiring), but only persist to disk when "完成" is
        /// clicked — same live-now/persist-on-Done split as hotkeys — so this drives an InfoBar
        /// reminder instead of leaving a silent toggle as the only feedback.</summary>
        [ObservableProperty]
        public partial bool ShowDisplaySettingsUnsavedHint { get; set; }

        private (bool Latency, bool AiUnlock, bool Group, bool FilterPanel)? _displaySettingsBaseline;

        private void UpdateDisplaySettingsUnsavedHint()
        {
            ShowDisplaySettingsUnsavedHint = _displaySettingsBaseline is { } baseline &&
                baseline != (ShowLatencyInDetails, ShowAiUnlockInDetails, ShowGroupInDetails, OpenServerFilterPanelOnStartup);
        }

        // ── Startup ───────────────────────────────────────────────────────────
        // The odd pair on this page: these two persist the moment they change instead of
        // on "完成". SetStartupEnabled creates or deletes a real Task Scheduler task, and
        // the panel can be dismissed with the back button without ever reaching Done — a
        // task that exists while settings.json says it doesn't just gets "corrected" away
        // by MainViewModel's reconcile on the next launch.

        /// <summary>Guards the change handlers when we write the value ourselves (initial
        /// load, external reconcile, failure rollback) rather than the user flipping it.</summary>
        private bool _isStartupInternalUpdate;

        /// <summary>Wired by MainViewModel to the control panel: id of the node xray is
        /// running right now, or null when stopped. Turning auto-connect on mid-session
        /// records it as the boot target — otherwise enabling it after connecting would
        /// leave nothing to connect to until the next manual connect.</summary>
        public Func<string?>? GetActiveServerId { get; set; }

        [ObservableProperty]
        public partial bool IsStartupEnabled { get; set; }

        [ObservableProperty]
        public partial bool IsAutoConnect { get; set; }

        partial void OnIsStartupEnabledChanged(bool value)
        {
            if (_isStartupInternalUpdate) return;
            _ = ApplyStartupAsync(value);
        }

        partial void OnIsAutoConnectChanged(bool value)
        {
            if (_isStartupInternalUpdate) return;
            _ = PersistAutoConnectAsync(value);
        }

        /// <summary>Serializes the two fire-and-forget writers below. Flipping a switch twice
        /// in quick succession otherwise lets a slow task registration finish after the newer
        /// gesture's save, leaving settings.json disagreeing with the Task Scheduler.</summary>
        private readonly SemaphoreSlim _startupWriteLock = new(1, 1);

        private async Task ApplyStartupAsync(bool enabled)
        {
            await _startupWriteLock.WaitAsync();
            try
            {
                try
                {
                    // Task registration is a COM RPC that can take hundreds of ms — the same
                    // reason MainViewModel reconciles off the critical path. Keep it off the
                    // UI thread so the toggle doesn't freeze mid-flip.
                    await Task.Run(() => _startup.SetStartupEnabled(enabled));
                }
                catch (Exception ex)
                {
                    await _dialogs.ShowErrorAsync(L.Startup_SetFailed, ex.Message);
                    // Put the switch back where the Task Scheduler actually left it.
                    SetStartupInternal(() => IsStartupEnabled = !enabled);
                    return;
                }

                var s = await _settings.LoadSettingsAsync();
                s.IsStartupEnabled = enabled;
                // Auto-connect without the boot task is dead state, and leaving it set would
                // make it silently come back on the next time autostart is enabled.
                if (!enabled)
                {
                    SetStartupInternal(() => IsAutoConnect = false);
                    s.IsAutoConnect = false;
                    s.LastAutoConnectServerId = null;
                }
                await _settings.SaveSettingsAsync(s);
            }
            finally
            {
                _startupWriteLock.Release();
            }
        }

        private async Task PersistAutoConnectAsync(bool enabled)
        {
            await _startupWriteLock.WaitAsync();
            try
            {
                var s = await _settings.LoadSettingsAsync();
                s.IsAutoConnect = enabled;
                if (!enabled)
                    s.LastAutoConnectServerId = null;
                else if (GetActiveServerId?.Invoke() is { } activeId)
                    s.LastAutoConnectServerId = activeId;
                // Enabling while stopped deliberately leaves the recorded target alone: the
                // next successful connect overwrites it anyway (ControlPanelViewModel).
                await _settings.SaveSettingsAsync(s);
            }
            finally
            {
                _startupWriteLock.Release();
            }
        }

        /// <summary>Adopts the Task Scheduler's own answer (external state is ground truth,
        /// see MainViewModel.ReconcileStartupTaskAsync). Internal write — the task already
        /// matches, so re-registering it would be a pointless second RPC.</summary>
        public void ApplyExternalStartupState(bool enabled) =>
            SetStartupInternal(() => IsStartupEnabled = enabled);

        /// <summary>Assigns a startup property without running its user-gesture side effect.</summary>
        private void SetStartupInternal(Action assign)
        {
            _isStartupInternalUpdate = true;
            try { assign(); }
            finally { _isStartupInternalUpdate = false; }
        }

        // ── Global hotkeys ────────────────────────────────────────────────────
        // No separate enabled flag — a hotkey is active whenever it has a combo assigned,
        // matching PowerToys' shortcut behavior. Assign via the recorder button (which auto-sets
        // on capture); clear via its right-click "清除快捷键" menu item (PersonalizeControl.xaml.cs).

        [ObservableProperty]
        public partial string HotkeyToggleDisplay { get; set; } = "";

        [ObservableProperty]
        public partial string HotkeyRestoreDisplay { get; set; } = "";

        /// <summary>True once a combo is recorded — drives the "+" assign-shortcut icon shown
        /// only in the unset state (PowerToys-style), hidden once a real combo is displayed.</summary>
        [ObservableProperty]
        public partial bool HotkeyToggleIsSet { get; set; }

        [ObservableProperty]
        public partial bool HotkeyRestoreIsSet { get; set; }

        /// <summary>Assigns the combo for <see cref="GlobalHotkeyStore.ToggleId"/> or
        /// <see cref="GlobalHotkeyStore.RestoreId"/> and notifies MainWindow to re-register.
        /// Caller (code-behind) is responsible for the actual user32 register/unregister probe.</summary>
        public void SetHotkey(int id, uint mods, uint vk)
        {
            GlobalHotkeyStore.SetCombo(id, mods, vk);
            RefreshDisplay(id);
            GlobalHotkeyStore.NotifyHotkeysChanged();
        }

        /// <summary>Resets the given hotkey back to unset. See <see cref="SetHotkey"/>.</summary>
        public void ClearHotkey(int id) => SetHotkey(id, 0, 0);

        private void RefreshDisplay(int id)
        {
            var (mods, vk) = GlobalHotkeyStore.GetCombo(id);
            var text = GlobalHotkeyStore.FormatDisplay(mods, vk);
            var isSet = !string.IsNullOrEmpty(text);
            var display = isSet ? text : L.Personalize_HotkeyNotSet;

            if (id == GlobalHotkeyStore.ToggleId)
            {
                HotkeyToggleIsSet = isSet;
                HotkeyToggleDisplay = display;
            }
            else
            {
                HotkeyRestoreIsSet = isSet;
                HotkeyRestoreDisplay = display;
            }
        }

        // ── Commands ──────────────────────────────────────────────────────────

        [RelayCommand]
        private void ResetColors()
        {
            SsColor        = Color.FromArgb(255,  96, 165, 250);
            VlessColor     = Color.FromArgb(255,  52, 211, 153);
            VmessColor     = Color.FromArgb(255, 167, 139, 250);
            Hysteria2Color = Color.FromArgb(255, 251, 146,  60);
            FallbackColor  = Color.FromArgb(255, 148, 163, 184);
        }

        public Task<string> ExportPresetAsync() =>
            new PresetExportService(_settings).ExportAsync();

        /// <summary>
        /// Drops the cache so hand-edits made while this panel was open are picked up, then
        /// reports whether settings.json is usable. <see cref="SettingsService"/> already refuses
        /// to save over a failed load, so this is not what prevents the data loss — it is what
        /// tells the user why their change went nowhere, at the one moment they can act on it.
        /// </summary>
        public async Task<bool> ValidateSettingsFileAsync()
        {
            var s = await _settings.ReloadAsync();

            if (s.IsFailedLoadFallback)
            {
                await _dialogs.ShowErrorAsync(L.Settings_InvalidTitle, L.Settings_InvalidMsg);
                return false;
            }

            return true;
        }

        public static bool PresetExists() => PresetImportService.PresetExists();

        /// <summary>
        /// Parses a Clash YAML config and appends its supported nodes to the saved server list
        /// (pure append, no dedupe — same semantics as "import from link"). Reuses the
        /// <see cref="PresetImported"/> reload path so the live list refreshes from disk.
        /// Returns (imported, skipped). Throws on invalid YAML — the caller surfaces it.
        /// Caller is expected to check <see cref="IsProxyRunning"/> and block before calling.
        /// </summary>
        public async Task<(int Imported, int Skipped)> ImportClashConfigAsync(string yamlText)
        {
            var parsed = ClashConfigParser.Parse(yamlText);

            if (parsed.Nodes.Count > 0)
            {
                // Imported nodes are manual entries (ServerEntry defaults SubscriptionId to "").
                var servers = await _settings.LoadServersAsync();
                servers.AddRange(parsed.Nodes);
                await _settings.SaveServersAsync(servers);
                PresetImported?.Invoke(this, EventArgs.Empty);
            }

            return (parsed.Nodes.Count, parsed.Skipped);
        }

        public async Task<PresetImportResult?> ConfirmAndImportPresetAsync()
        {
            var confirmed = await _dialogs.ShowConfirmationAsync(
                L.Confirm_ReplaceTitle,
                L.Confirm_ReplaceMsg,
                L.Dialog_Replace,
                L.Dialog_Cancel,
                isDanger: true);
            if (!confirmed)
                return null;

            var result = await new PresetImportService(_settings).ApplyAsync();
            PresetImported?.Invoke(this, EventArgs.Empty);
            return result;
        }

        [RelayCommand]
        private async Task Done()
        {
            // The two startup writers persist on change under this lock, and one of them can
            // still be in flight here. Hold it across the whole read-modify-write below, or
            // ValidateSettingsFileAsync's reload reads the pre-flip file back off disk and the
            // save below puts it there for good. Re-stating the two flags is not enough on its
            // own: LastAutoConnectServerId is written by those paths too and has no counterpart
            // here to restore it.
            await _startupWriteLock.WaitAsync();
            try
            {
                await SaveAndCloseAsync();
            }
            finally
            {
                _startupWriteLock.Release();
            }
        }

        private async Task SaveAndCloseAsync()
        {
            // On a settings.json the user has left unparseable the save below is refused, so
            // bail here where there is still somewhere to say why. Doubles as the cache drop
            // that makes the load pick up hand-edits made while this panel was open, so Done
            // cannot write stale values back over freshly hand-edited ones.
            if (!await ValidateSettingsFileAsync()) return;

            var s = await _settings.LoadSettingsAsync();
            ProtocolColorStore.SaveTo(s);
            GlobalHotkeyStore.SaveTo(s);
            s.ThemeSetting = ThemeHelper.CurrentTheme switch
            {
                ElementTheme.Light   => "Light",
                ElementTheme.Dark    => "Dark",
                _                    => "Default"
            };
            s.BackdropSetting = ThemeHelper.CurrentBackdrop;
            // Redundant while Done holds the startup writers' lock, but kept: these are the
            // authoritative UI values regardless of what the reload above returned.
            s.IsStartupEnabled = IsStartupEnabled;
            s.IsAutoConnect = IsAutoConnect;
            s.ShowLatencyInDetails = ShowLatencyInDetails;
            s.ShowAiUnlockInDetails = ShowAiUnlockInDetails;
            s.ShowGroupInDetails = ShowGroupInDetails;
            s.OpenServerFilterPanelOnStartup = OpenServerFilterPanelOnStartup;
            // Re-baseline so the unsaved-changes hint clears now that these match disk —
            // otherwise reopening Personalize later would show a stale "unsaved" hint for
            // values that were, in fact, already saved here.
            _displaySettingsBaseline = (ShowLatencyInDetails, ShowAiUnlockInDetails, ShowGroupInDetails, OpenServerFilterPanelOnStartup);
            ShowDisplaySettingsUnsavedHint = false;
            // Language and region don't take effect until the next process start, but Done
            // still persists them — otherwise the user would have to click the restart hint
            // to save at all, which is surprising compared to how Theme / Backdrop behave.
            s.Language = LanguageHelper.TagAt(SelectedLanguageIndex);
            s.RoutingRegion = SelectedRegionCode;
            await _settings.SaveSettingsAsync(s);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── Initialization ────────────────────────────────────────────────────

        public void LoadFromStore()
        {
            SsColor        = ProtocolColorStore.Ss;
            VlessColor     = ProtocolColorStore.Vless;
            VmessColor     = ProtocolColorStore.Vmess;
            Hysteria2Color = ProtocolColorStore.Hysteria2;
            FallbackColor  = ProtocolColorStore.Fallback;

            SelectedThemeIndex = ThemeHelper.CurrentTheme switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark  => 1,
                _                  => 2,
            };

            SelectedBackdropIndex = ThemeHelper.CurrentBackdrop == "Acrylic" ? 1 : 0;

            RefreshDisplay(GlobalHotkeyStore.ToggleId);
            RefreshDisplay(GlobalHotkeyStore.RestoreId);
        }

        public void LoadDisplayOptions(AppSettings settings)
        {
            ShowLatencyInDetails = settings.ShowLatencyInDetails;
            ShowAiUnlockInDetails = settings.ShowAiUnlockInDetails;
            ShowGroupInDetails = settings.ShowGroupInDetails;
            OpenServerFilterPanelOnStartup = settings.OpenServerFilterPanelOnStartup;
            _displaySettingsBaseline = (ShowLatencyInDetails, ShowAiUnlockInDetails, ShowGroupInDetails, OpenServerFilterPanelOnStartup);
        }

        /// <summary>Shows the persisted autostart state. Internal write — displaying what
        /// was saved must not re-register the task.</summary>
        public void LoadStartup(AppSettings settings) => SetStartupInternal(() =>
        {
            IsStartupEnabled = settings.IsStartupEnabled;
            IsAutoConnect    = settings.IsAutoConnect;
        });

        public void LoadLanguage(AppSettings settings)
        {
            // Assign through the field to bypass the setter's InfoBar side effect, then
            // record this as the baseline so divergence-from-baseline drives the hint.
            var index = LanguageHelper.IndexOf(settings.Language);
            _suppressLanguageRestartHint = true;
            SelectedLanguageIndex = index;
            _suppressLanguageRestartHint = false;
            _initialLanguageIndex = index;
        }

        public void LoadRegion(AppSettings settings)
        {
            // Mirror LoadLanguage: assign suppressed, then record the baseline so the restart
            // hint tracks divergence-from-baseline rather than "user touched the dropdown".
            var index = Array.IndexOf(RegionCodes, settings.RoutingRegion);
            if (index < 0) index = 0;
            _suppressRegionRestartHint = true;
            SelectedRegionIndex = index;
            _suppressRegionRestartHint = false;
            _initialRegionIndex = index;
        }
    }
}
