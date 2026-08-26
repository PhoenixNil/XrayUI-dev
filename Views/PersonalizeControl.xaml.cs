using System;
using Microsoft.UI.Xaml.Automation;
using Windows.Storage;
using Windows.Storage.Pickers;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class PersonalizeControl
    {
        public PersonalizeViewModel ViewModel { get; set; } = null!;

        public string OnOffLabel(bool isOn) => isOn ? L.Dialog_On : L.Dialog_Off;

        public PersonalizeControl()
        {
            this.InitializeComponent();

            AutomationProperties.SetName(AppLanguageExpander, L.Personalize_LanguageRegionExpanderAutomationName);
            AutomationProperties.SetName(ExportPresetButton, L.Personalize_ExportTooltip);
            AutomationProperties.SetName(ImportDropDownButton, L.Personalize_ImportTooltip);

            AutomationProperties.SetName(ToggleHotkeyButton, L.Personalize_HotkeyToggleAutomationName);
            AutomationProperties.SetName(RestoreHotkeyButton, L.Personalize_HotkeyRestoreAutomationName);
            ToolTipService.SetToolTip(ToggleHotkeyButton, L.Personalize_HotkeyRecordTooltip);
            ToolTipService.SetToolTip(RestoreHotkeyButton, L.Personalize_HotkeyRecordTooltip);
        }

        private async void ExportPresetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportDir = await ViewModel.ExportPresetAsync();
                ShowInfo(InfoBarSeverity.Success,
                    L.Personalize_ExportSuccess,
                    Loc.Format("Personalize_ExportSuccessMsgFmt", exportDir));
            }
            catch (Exception ex)
            {
                ShowInfo(InfoBarSeverity.Error, L.Error_ExportFailed, ex.Message);
            }
        }

        private async void ImportPresetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!PersonalizeViewModel.PresetExists())
                {
                    ShowInfo(InfoBarSeverity.Warning,
                        L.Personalize_PresetMissingTitle,
                        L.Personalize_PresetMissingMsg);
                    return;
                }

                if (ViewModel.IsProxyRunning?.Invoke() == true)
                {
                    ShowInfo(InfoBarSeverity.Warning,
                        L.Personalize_ImportBlockedTitle,
                        L.Personalize_ImportBlockedMsg);
                    return;
                }

                var result = await ViewModel.ConfirmAndImportPresetAsync();
                if (result is null) return;

                var advanced = result.ImportedAdvancedRouting ? L.Personalize_ImportAdvancedSuffix : "";
                ShowInfo(InfoBarSeverity.Success,
                    L.Personalize_ImportSuccess,
                    Loc.Format("Personalize_ImportSuccessMsg",
                        result.ImportedServers,
                        result.ImportedSubscriptions,
                        result.ImportedCustomRules,
                        advanced));
            }
            catch (Exception ex)
            {
                ShowInfo(InfoBarSeverity.Error, L.Personalize_ImportFailed, ex.Message);
            }
        }

        private async void ImportClashConfig_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel.IsProxyRunning?.Invoke() == true)
                {
                    ShowInfo(InfoBarSeverity.Warning,
                        L.Personalize_ImportBlockedTitle,
                        L.Personalize_ImportBlockedMsg);
                    return;
                }

                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };
                picker.FileTypeFilter.Add(".yaml");
                picker.FileTypeFilter.Add(".yml");

                // Unpackaged app: the picker must be associated with the host window's HWND.
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(ThemeHelper.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSingleFileAsync();
                if (file is null) return;

                var text = await FileIO.ReadTextAsync(file);
                var (imported, skipped) = await ViewModel.ImportClashConfigAsync(text);

                if (imported == 0)
                {
                    ShowInfo(InfoBarSeverity.Warning,
                        L.Personalize_ClashImportNoNodesTitle,
                        L.Personalize_ClashImportNoNodesMsg);
                    return;
                }

                ShowInfo(InfoBarSeverity.Success,
                    L.Personalize_ClashImportSuccess,
                    Loc.Format("Personalize_ClashImportSuccessMsg", imported, skipped));
            }
            catch (Exception ex)
            {
                ShowInfo(InfoBarSeverity.Error, L.Personalize_ClashImportFailed, ex.Message);
            }
        }

        private void ShowInfo(InfoBarSeverity severity, string title, string message)
        {
            OperationInfoBar.Severity = severity;
            OperationInfoBar.Title = title;
            OperationInfoBar.Message = message;
            OperationInfoBar.IsOpen = true;
        }

        private async void LanguageRestartButton_Click(object sender, RoutedEventArgs e)
        {
            // Restarting after a refused save would bring the app back on the old language and
            // region with no explanation. ApplyPendingChangesAsync has already said why.
            if (!await ViewModel.ApplyPendingChangesAsync()) return;
            App.Restart();
        }

        // ── Hotkeys ───────────────────────────────────────────────────────────
        // Capture happens in a dialog (HotkeyRecorderControl, shown via IDialogService) rather
        // than inline on the row — easier to discover than "click then press keys" for users
        // unfamiliar with the pattern. The dialog itself has no Win32 knowledge; the actual
        // RegisterHotKey probe happens here, after it closes, so a conflict can be reported and
        // the previous (unchanged) binding re-asserted without the dialog needing to know about it.

        private async void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            var id = ReferenceEquals(sender, ToggleHotkeyButton) ? GlobalHotkeyStore.ToggleId : GlobalHotkeyStore.RestoreId;
            var (mods, vk) = GlobalHotkeyStore.GetCombo(id);
            var result = await ViewModel.Dialogs.ShowHotkeyRecorderDialogAsync(L.Personalize_HotkeysDialogTitle, mods, vk);
            if (result is null) return; // cancelled — nothing touched

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(ThemeHelper.MainWindow);

            if (result.Value.cleared)
            {
                HotkeyInterop.UnregisterHotKey(hWnd, id);
                ViewModel.ClearHotkey(id);
                ShowHotkeySaved(L.Personalize_HotkeyClearedMsg);
                return;
            }

            HotkeyInterop.UnregisterHotKey(hWnd, id);
            if (!HotkeyInterop.RegisterHotKey(hWnd, id, result.Value.mods | GlobalHotkeyStore.ModNoRepeat, result.Value.vk))
            {
                await ViewModel.Dialogs.ShowErrorAsync(L.Personalize_HotkeyConflictTitle, L.Personalize_HotkeyConflictMsg);
                // Store wasn't mutated — re-assert whatever was previously registered for this id.
                GlobalHotkeyStore.NotifyHotkeysChanged();
                return;
            }

            ViewModel.SetHotkey(id, result.Value.mods, result.Value.vk);
            ShowHotkeySaved(L.Personalize_HotkeySavedMsg);
        }

        // The hotkey is live the moment this fires (RegisterHotKey already succeeded above) —
        // independent of the page's "完成" button, which only persists it to disk for next
        // launch. This confirms that to the user instead of leaving the button's silent text
        // change as the only feedback.
        private void ShowHotkeySaved(string message)
        {
            HotkeySavedInfoBar.Message = message;
            HotkeySavedInfoBar.IsOpen = true;
        }
    }
}
