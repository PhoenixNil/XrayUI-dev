using System;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using WinUIEx;
using XrayUI.Helpers;

namespace XrayUI.Views
{
    public sealed partial class ConfigProfileWindow
    {
        private readonly Window _owner;

        // Set while the VM's own Close command is closing us, so the Closed handler does not
        // ask about unsaved changes a second time.
        private bool _closeConfirmed;

        public ConfigProfileViewModel ViewModel { get; }

        public ConfigProfileWindow(Window owner, ConfigProfileViewModel viewModel, bool tunSlot)
        {
            ViewModel = viewModel;
            // Before InitializeComponent: x:Bind initializes the Segmented from the VM as it
            // parses, and seeding afterwards loses the race with the control's own selection.
            ViewModel.SetInitialSlot(tunSlot);
            this.InitializeComponent();
            _owner = owner;

            this.SetWindowSize(760, 620);
            AppWindow.Title = L.ConfigProfile_Title;
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "output.ico"));
            ThemeHelper.FollowAppTheme(this, WindowRoot);
            // Set the backdrop in code, AFTER FollowAppTheme has applied the correct theme.
            // Declaring it in XAML paints Mica in the default theme first, then visibly retints
            // when the theme switches — the unwanted transition flash. Mirrors CustomRulesWindow.
            SystemBackdrop = new MicaBackdrop();

            // Set here rather than through x:Uid: the attached-property resw form
            // ("[Using:...]Foo.Bar") does not survive startup in this project.
            ToolTipService.SetToolTip(OpenFolderButton, L.ConfigProfile_OpenFolderTooltip);
            ToolTipService.SetToolTip(PreviewButton, L.ConfigProfile_PreviewTooltip);
            AutomationProperties.SetName(OpenFolderButton, L.ConfigProfile_OpenFolderTooltip);
            AutomationProperties.SetName(PreviewButton, L.ConfigProfile_PreviewTooltip);
            AutomationProperties.SetName(EditorTextBox, L.ConfigProfile_EditorAutomationName);

            this.ShowAsOwnedModal(owner);

            // Let the VM route its error dialogs to this window's XamlRoot instead of
            // falling back to MainWindow's — otherwise they render behind.
            ViewModel.GetXamlRoot = () => Content?.XamlRoot;
            ViewModel.CloseRequested += OnCloseRequested;
            ViewModel.FolderOpened += OnFolderOpened;

            _ = ViewModel.LoadAsync();

            this.Closed += OnClosed;
            this.Activated += OnWindowActivated;
            AppWindow.Closing += OnAppWindowClosing;
        }

        /// <summary>
        /// Title-bar close. Cancels the OS close, asks about unsaved edits, and only closes for
        /// real once the user has answered — the prompt is async, so the synchronous Closing
        /// callback cannot wait for it.
        /// </summary>
        private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_closeConfirmed) return;

            args.Cancel = true;

            if (!await ViewModel.ConfirmCloseAsync()) return;

            _closeConfirmed = true;
            Close();
        }

        /// <summary>
        /// Closes without asking about unsaved edits. Used by hide-to-tray, which cannot wait on
        /// a dialog and must not leave a modal window owned by a hidden parent. Discarding
        /// matches CustomRulesWindow, which drops unsaved rule edits on the same path.
        /// </summary>
        public void ForceClose()
        {
            _closeConfirmed = true;
            Close();
        }

        private bool _reloadAfterFolderOpen;

        private void OnFolderOpened(object? sender, EventArgs e)
        {
            _reloadAfterFolderOpen = true;
        }

        /// <summary>
        /// After the profiles folder is opened, re-read the file once when the window returns to
        /// the foreground — otherwise an edit made in an external editor is invisible here, and
        /// the next Save writes the stale editor content straight over it. The flag is single-use
        /// and only set by an actual folder open, so ordinary alt-tabbing is a no-op. Mirrors
        /// CustomRulesWindow, which solves the same problem for settings.json.
        /// </summary>
        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (!_reloadAfterFolderOpen) return;
            _reloadAfterFolderOpen = false;

            try
            {
                await ViewModel.ReloadFromDiskAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigProfile] ReloadFromDiskAsync failed: {ex.Message}");
            }
        }

        private void OnCloseRequested(object? sender, EventArgs e)
        {
            // The VM already confirmed; skip the Closing prompt.
            _closeConfirmed = true;
            Close();
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            AppWindow.Closing -= OnAppWindowClosing;
            this.Activated -= OnWindowActivated;
            ViewModel.CloseRequested -= OnCloseRequested;
            ViewModel.FolderOpened -= OnFolderOpened;
            _owner.Activate();
        }
    }
}
