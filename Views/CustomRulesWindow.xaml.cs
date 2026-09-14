using System;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using WinUIEx;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class CustomRulesWindow
    {
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

            this.ShowAsOwnedModal(owner);

            // Let the VM route its error dialogs to this window's XamlRoot instead of
            // falling back to MainWindow's — otherwise they render behind.
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

    }
}
