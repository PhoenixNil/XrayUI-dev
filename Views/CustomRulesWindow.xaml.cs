using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.ViewModels;

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

            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = DpiHelper.GetWindowScale(hWnd);
            AppWindow.Resize(new SizeInt32(
                (int)Math.Round(620 * scale),
                (int)Math.Round(460 * scale)));
            AppWindow.Title = "自定义路由规则";
			AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

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

            // Initial load — fire-and-forget; LoadAsync populates Rules + IsEffectiveNow.
            _ = ViewModel.LoadAsync();

            this.Closed += OnClosed;
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            ViewModel.ShowAddOrEditDialogRequested -= OnShowAddOrEditDialogRequested;
            ViewModel.CloseRequested               -= OnCloseRequested;
            _owner.Activate();
        }

        private void OnCloseRequested(object? sender, EventArgs e) => Close();

        private async void OnShowAddOrEditDialogRequested(object? sender, CustomRoutingRule? existing)
        {
            var dialog = new AddRuleDialog(existing) { XamlRoot = Content.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || dialog.Result is null) return;

            if (existing is null)
                ViewModel.AddNewRule(dialog.Result);
            else
                ViewModel.ReplaceRule(existing, dialog.Result);
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
