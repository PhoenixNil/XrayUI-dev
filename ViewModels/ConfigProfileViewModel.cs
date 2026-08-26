using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    /// <summary>
    /// Backs the config-profile window: two fixed slots (TUN and system proxy), each either
    /// running the generated config or a hand-written profile from
    /// <c>%LocalAppData%\XrayUI\profiles\</c>.
    ///
    /// Save is the only path that writes, and it always validates first — a profile that is
    /// enabled is a config xray will be handed verbatim, so there is no "save it broken and fix
    /// it later" state to fall into. Disabling a profile the user has already broken is done by
    /// resetting it to the generated template.
    /// </summary>
    public partial class ConfigProfileViewModel : ObservableObject
    {
        private readonly SettingsService _settings;
        private readonly ConfigProfileStore _profiles;
        private readonly IDialogService _dialogs;

        /// <summary>Lets error dialogs root in the profile window instead of MainWindow,
        /// which would render them behind it.</summary>
        public Func<XamlRoot?>? GetXamlRoot { get; set; }

        /// <summary>Returns the config the selected node would run next, or null when nothing
        /// is selected.</summary>
        private readonly Func<Task<string?>> _buildConfigPreviewAsync;

        public event EventHandler? CloseRequested;

        /// <summary>Raised when the profiles folder is opened, so the window knows an external
        /// edit may be coming and can re-read the file when it comes back to the foreground.</summary>
        public event EventHandler? FolderOpened;

        /// <summary>Raised after a save commits, so ControlPanelViewModel can re-gate the menu
        /// items a profile takes ownership of.</summary>
        public event EventHandler<ConfigProfileState>? ProfileStateChanged;

        public readonly record struct ConfigProfileState(bool UseTunProfile, bool UseProxyProfile);

        /// <summary>Suppresses the slot-changed handler while the VM itself moves the
        /// Segmented, so a programmatic move is not mistaken for the user picking a slot.</summary>
        private bool _isSlotSwitchInternal;

        public ConfigProfileViewModel(
            SettingsService settings,
            ConfigProfileStore profiles,
            IDialogService dialogs,
            Func<Task<string?>> buildConfigPreviewAsync)
        {
            _settings = settings;
            _profiles = profiles;
            _dialogs = dialogs;
            _buildConfigPreviewAsync = buildConfigPreviewAsync;
            EditorText = string.Empty;
        }

        // ── Slot selection ────────────────────────────────────────────────────

        /// <summary>Slot indices, in the order the Segmented declares its items. Named rather
        /// than spelled 0/1 inline: reordering the two segments in XAML has to flip the mapping
        /// in three places at once, and bare literals give no hint of that.</summary>
        private const int ProxySlotIndex = 0;
        private const int TunSlotIndex = 1;

        /// <summary>Which slot the editor is showing. An index rather than a bool because it
        /// binds straight to Segmented.SelectedIndex, the same shape the theme picker uses.</summary>
        [ObservableProperty]
        public partial int SelectedSlotIndex { get; set; }

        /// <summary>TUN and system proxy are separate configs because they describe different
        /// inbound stacks. Which one a slot requires is not spelled out in the window: the
        /// segment label says it, and getting it wrong is a hard save-time error naming the
        /// exact problem, so a permanent line of chrome would only repeat the validator.</summary>
        public bool IsTunSlot => SelectedSlotIndex == TunSlotIndex;

        /// <summary>Whether the shown slot runs its profile instead of the generated config.
        /// Committed by Save, not on toggle, so enabling always passes validation first.</summary>
        [ObservableProperty]
        public partial bool IsProfileEnabled { get; set; }

        partial void OnIsProfileEnabledChanged(bool value) => IsDirty = true;

        partial void OnSelectedSlotIndexChanged(int value)
        {
            OnPropertyChanged(nameof(IsTunSlot));
            if (_isSlotSwitchInternal) return;
            _ = SwitchSlotAsync(value);
        }

        private async Task SwitchSlotAsync(int slotIndex)
        {
            if (IsDirty && !await ConfirmDiscardAsync())
            {
                // Put the segmented control back without re-entering the changed handler.
                SetSlotSilently(slotIndex == TunSlotIndex ? ProxySlotIndex : TunSlotIndex);
                return;
            }

            await LoadSlotAsync();
        }

        // ── Editor state ──────────────────────────────────────────────────────

        [ObservableProperty]
        public partial string EditorText { get; set; }

        partial void OnEditorTextChanged(string value) => IsDirty = true;

        [ObservableProperty]
        public partial bool IsDirty { get; private set; }

        [ObservableProperty]
        public partial bool IsValidationOpen { get; private set; }

        [ObservableProperty]
        public partial string ValidationMessage { get; private set; } = string.Empty;

        [ObservableProperty]
        public partial InfoBarSeverity ValidationSeverity { get; private set; }

        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Picks the slot to open on — the current mode, so the config the user is about to run
        /// is the one they see first. Must be called before the window runs InitializeComponent:
        /// Segmented settles its own SelectedIndex as it realizes and pushes that back through
        /// the TwoWay binding, which lands after an async load and silently drags the editor to
        /// the other slot. Seeding it up front is the same "populate the VM before x:Bind parses"
        /// rule MainWindow follows.
        /// </summary>
        public void SetInitialSlot(bool tunSlot) =>
            SetSlotSilently(tunSlot ? TunSlotIndex : ProxySlotIndex);

        public Task LoadAsync() => LoadSlotAsync();

        private async Task LoadSlotAsync()
        {
            var tunSlot = IsTunSlot;
            var settings = await _settings.LoadSettingsAsync();

            string text;
            try
            {
                // No file yet: seed with the generated config for this mode so the user starts
                // from something that already works rather than a blank page.
                text = await _profiles.ReadAsync(tunSlot)
                       ?? XrayConfigBuilder.BuildProfileTemplate(settings, tunSlot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigProfile] Load slot failed: {ex}");
                text = string.Empty;
                ShowError(L.ConfigProfile_LoadFailed);
            }

            EditorText = text;
            IsProfileEnabled = tunSlot ? settings.UseTunConfigProfile : settings.UseProxyConfigProfile;

            // Both assignments above flag the editor dirty; this is the baseline for the slot
            // that was just loaded, so clear it once here rather than suppressing each setter.
            IsDirty = false;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task Save()
        {
            var tunSlot = IsTunSlot;
            var result = ConfigProfileJson.Validate(EditorText, tunSlot);

            if (!result.IsValid)
            {
                ShowError(DescribeError(result));
                return;
            }

            ConfigProfileState state;
            try
            {
                // The user's own text, not a reserialization of it — their formatting and
                // comment-free layout survive the round trip.
                await _profiles.WriteAsync(tunSlot, EditorText);

                var settings = await _settings.LoadSettingsAsync();
                if (tunSlot) settings.UseTunConfigProfile = IsProfileEnabled;
                else settings.UseProxyConfigProfile = IsProfileEnabled;

                if (!await _settings.SaveSettingsAsync(settings))
                {
                    await ShowDialogErrorAsync(L.ConfigProfile_SaveFailedTitle, L.ConfigProfile_SettingsUnwritable);
                    return;
                }

                state = new ConfigProfileState(
                    settings.UseTunConfigProfile, settings.UseProxyConfigProfile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigProfile] Save failed: {ex}");
                await ShowDialogErrorAsync(L.ConfigProfile_SaveFailedTitle, ex.Message);
                return;
            }

            IsDirty = false;
            ProfileStateChanged?.Invoke(this, state);

            // "takes effect on the next connect" is only true when the slot is actually enabled;
            // saving with the switch off writes the file but changes nothing about what runs.
            var saved = IsProfileEnabled ? L.ConfigProfile_Saved : L.ConfigProfile_SavedInactive;

            // Warnings do not block the save, so they carry the confirmation too — a lone warning
            // bar leaves the user unsure whether anything was written.
            var warnings = DescribeWarnings(result.Warnings);
            if (warnings is null) ShowInfo(saved);
            else ShowWarning($"{saved} {warnings}");
        }

        [RelayCommand]
        private async Task ResetToDefault()
        {
            if (!await _dialogs.ShowConfirmationAsync(
                    L.ConfigProfile_ResetTitle, L.ConfigProfile_ResetMsg,
                    xamlRoot: GetXamlRoot?.Invoke()))
                return;

            var settings = await _settings.LoadSettingsAsync();
            EditorText = XrayConfigBuilder.BuildProfileTemplate(settings, IsTunSlot);
            ClearValidation();
        }

        [RelayCommand]
        private async Task Preview()
        {
            // The preview builds from what is on disk, so unsaved edits would not appear in it.
            if (IsDirty)
            {
                await ShowDialogErrorAsync(L.ConfigProfile_PreviewStaleTitle, L.ConfigProfile_PreviewStaleMsg);
                return;
            }

            try
            {
                var configJson = await _buildConfigPreviewAsync();
                if (configJson is null)
                {
                    await ShowDialogErrorAsync(L.ConfigProfile_PreviewTitle, L.ConfigProfile_PreviewNoServer);
                    return;
                }

                Directory.CreateDirectory(AppPaths.LocalAppDataDir);
                await File.WriteAllTextAsync(AppPaths.XrayConfigPreviewPath, configJson);

                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.XrayConfigPreviewPath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigProfile] Preview failed: {ex}");
                await ShowDialogErrorAsync(L.ConfigProfile_PreviewTitle, ex.Message);
            }
        }

        [RelayCommand]
        private void OpenFolder()
        {
            ConfigProfileStore.OpenFolder();
            FolderOpened?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Re-reads the current slot from disk after the user has been editing the file outside
        /// the app. Refuses when the editor itself has unsaved changes: silently replacing them
        /// would trade one kind of lost work for another, so the two are surfaced instead and the
        /// user decides which to keep.
        /// </summary>
        public async Task ReloadFromDiskAsync()
        {
            if (IsDirty)
            {
                ShowWarning(L.ConfigProfile_ExternalChangeWarning);
                return;
            }

            await LoadSlotAsync();
        }

        [RelayCommand]
        private async Task Close()
        {
            if (!await ConfirmCloseAsync()) return;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Called by the window when the user closes it from the title bar.</summary>
        public Task<bool> ConfirmCloseAsync() => IsDirty ? ConfirmDiscardAsync() : Task.FromResult(true);

        // ── Helpers ───────────────────────────────────────────────────────────

        private Task<bool> ConfirmDiscardAsync() =>
            _dialogs.ShowConfirmationAsync(
                L.ConfigProfile_DiscardTitle, L.ConfigProfile_DiscardMsg, isDanger: true,
                xamlRoot: GetXamlRoot?.Invoke());

        private void SetSlotSilently(int slotIndex)
        {
            _isSlotSwitchInternal = true;
            try { SelectedSlotIndex = slotIndex; }
            finally { _isSlotSwitchInternal = false; }
        }

        private Task ShowDialogErrorAsync(string title, string message) =>
            _dialogs.ShowErrorAsync(title, message, GetXamlRoot?.Invoke());

        private void ShowError(string message) => SetValidation(message, InfoBarSeverity.Error);
        private void ShowWarning(string message) => SetValidation(message, InfoBarSeverity.Warning);
        private void ShowInfo(string message) => SetValidation(message, InfoBarSeverity.Success);

        private void SetValidation(string message, InfoBarSeverity severity)
        {
            ValidationMessage = message;
            ValidationSeverity = severity;
            IsValidationOpen = true;
        }

        private void ClearValidation() => IsValidationOpen = false;

        private static string DescribeError(ConfigProfileResult result) => result.Error switch
        {
            ConfigProfileError.Empty => L.ConfigProfile_ErrEmpty,
            ConfigProfileError.RootMustBeObject => L.ConfigProfile_ErrRootMustBeObject,
            ConfigProfileError.OutboundsNotAllowed => L.ConfigProfile_ErrOutboundsNotAllowed,
            ConfigProfileError.InboundsMissing => L.ConfigProfile_ErrInboundsMissing,
            ConfigProfileError.TunInboundMissing => L.ConfigProfile_ErrTunInboundMissing,
            ConfigProfileError.TunInboundNotAllowed => L.ConfigProfile_ErrTunInboundNotAllowed,
            ConfigProfileError.TunInterfaceNameMismatch =>
                Loc.Format("ConfigProfile_ErrTunNameMismatch", XrayConfigConstants.TunInterfaceName),
            // InvalidJson is the only kind left, and it always carries the parser's own message.
            _ => result.Detail!,
        };

        private static string? DescribeWarnings(ConfigProfileWarning warnings)
        {
            if (warnings == ConfigProfileWarning.None) return null;

            var parts = new System.Collections.Generic.List<string>(3);
            if (warnings.HasFlag(ConfigProfileWarning.NoSystemProxyInbound))
                parts.Add(L.ConfigProfile_WarnNoSystemProxyInbound);
            if (warnings.HasFlag(ConfigProfileWarning.NoAutoSystemRouting))
                parts.Add(L.ConfigProfile_WarnNoAutoSystemRouting);
            if (warnings.HasFlag(ConfigProfileWarning.UnknownOutboundTag))
                parts.Add(Loc.Format(
                    "ConfigProfile_WarnUnknownOutboundTag",
                    string.Join(", ", ConfigProfileJson.InjectedOutboundTags)));

            return string.Join(" ", parts);
        }
    }
}
