using System;
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

        public PersonalizeViewModel(IDialogService dialogs, SettingsService settings)
        {
            _dialogs = dialogs;
            _settings = settings;
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

        /// <summary>Persist the currently-selected language and routing region. Call right before
        /// <see cref="App.Restart"/> — both only take effect on the next process start.</summary>
        public async Task ApplyPendingChangesAsync()
        {
            var s = await _settings.LoadSettingsAsync();
            s.Language = LanguageHelper.TagAt(SelectedLanguageIndex);
            s.RoutingRegion = SelectedRegionCode;
            await _settings.SaveSettingsAsync(s);
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
