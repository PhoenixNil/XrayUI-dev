using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class CustomRulesWindow
    {
        private const int GWLP_HWNDPARENT = -8;

        [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private readonly Window _owner;

        public CustomRulesViewModel ViewModel { get; }

        public CustomRulesWindow(Window owner, CustomRulesViewModel viewModel)
        {
            ViewModel = viewModel;
            this.InitializeComponent();
            _owner = owner;

            this.SetWindowSize(620, 460);
            AppWindow.Title = L.CustomRules_Title;
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "output.ico"));
            ThemeHelper.FollowAppTheme(this, WindowRoot);
            // Set the backdrop in code, AFTER FollowAppTheme has applied the correct theme.
            // Declaring it in XAML paints Mica in the default theme first, then visibly retints
            // when the theme switches — the unwanted transition flash. Mirrors LogWindow.
            SystemBackdrop = new MicaBackdrop();

            ToolTipService.SetToolTip(OpenAdvancedEditorButton, L.CustomRules_AdvancedEditorTooltip);
            ToolTipService.SetToolTip(UpdateGeoButton,          L.CustomRules_UpdateGeoTooltip);

			var presenter = OverlappedPresenter.CreateForDialog();

            // 1. Set Win32 owner BEFORE IsModal — IsModal requires an owner.
            SetWindowOwner(owner);

            // 2. Mark presenter modal, then commit it to the AppWindow.
            presenter.IsModal = true;
            AppWindow.SetPresenter(presenter);

            // 3. Show via AppWindow.Show() to apply the modal presenter at the OS level.
            //    Window.Activate() doesn't reliably re-apply IsModal once the
            //    window has any prior presenter state.
            AppWindow.Show();

            // Let the VM route dialogs (progress / success / error) to this window's XamlRoot
            // instead of falling back to MainWindow's — otherwise they render behind.
            ViewModel.GetXamlRoot = () => Content?.XamlRoot;

            // VM events
            ViewModel.ShowAddOrEditDialogRequested += OnShowAddOrEditDialogRequested;
            ViewModel.CloseRequested               += OnCloseRequested;
            ViewModel.AdvancedEditorOpened         += OnAdvancedEditorOpened;

            // Initial load — fire-and-forget; LoadAsync populates Rules + IsEffectiveNow.
            _ = ViewModel.LoadAsync();

            this.Closed    += OnClosed;
            this.Activated += OnWindowActivated;
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            this.Activated                         -= OnWindowActivated;
            ViewModel.ShowAddOrEditDialogRequested -= OnShowAddOrEditDialogRequested;
            ViewModel.CloseRequested               -= OnCloseRequested;
            ViewModel.AdvancedEditorOpened         -= OnAdvancedEditorOpened;
            _owner.Activate();
        }

        private bool _reloadAfterAdvancedEditor;

        private void OnAdvancedEditorOpened(object? sender, EventArgs e)
        {
            _reloadAfterAdvancedEditor = true;
        }

        /// <summary>
        /// After the advanced editor opens settings.json, reload once when the window
        /// returns to the foreground. The flag is single-use — only set on successful
        /// editor launch and cleared after the reload — so alt-tab activations that
        /// aren't preceded by an editor open are no-ops.
        /// </summary>
        private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (!_reloadAfterAdvancedEditor) return;
            _reloadAfterAdvancedEditor = false;

            try
            {
                await ViewModel.ReloadFromDiskAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CustomRulesWindow] ReloadFromDiskAsync failed: {ex.Message}");
            }
        }

        private void OnCloseRequested(object? sender, EventArgs e) => Close();

        private async void OnShowAddOrEditDialogRequested(object? sender, CustomRoutingRule? existing)
        {
            var hostHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dialog = new AddRuleDialog(hostHwnd, existing) { XamlRoot = Content.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || dialog.Result is null) return;

            if (existing is null)
                ViewModel.AddNewRule(dialog.Result);
            else
                ViewModel.ReplaceRule(existing, dialog.Result);
        }

        private void EditRuleButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                ToolTipService.SetToolTip(element, L.CustomRules_EditRowTooltip);
        }

        private void DeleteRuleButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                ToolTipService.SetToolTip(element, L.CustomRules_DeleteRowTooltip);
        }

        private void EditRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: CustomRoutingRule rule })
                ViewModel.EditRuleCommand.Execute(rule);
        }

        private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: CustomRoutingRule rule })
                ViewModel.DeleteRuleCommand.Execute(rule);
        }

        // ── Update GeoFiles ──────────────────────────────────────────────────

        private void UpdateGeoButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.UpdateGeoDataCommand.Execute(null);
        }

        private void SetWindowOwner(Window owner)
        {
            var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            var ownedHwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

            if (IntPtr.Size == 8)
                SetWindowLongPtr(ownedHwnd, GWLP_HWNDPARENT, ownerHwnd);
            else
                SetWindowLong(ownedHwnd, GWLP_HWNDPARENT, ownerHwnd);
        }

    }
}
