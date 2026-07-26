using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class ManageSubscriptionsDialog
    {
        public ManageSubscriptionsViewModel ViewModel { get; }

        public ManageSubscriptionsDialog(ManageSubscriptionsViewModel vm)
        {
            ViewModel = vm;
            InitializeComponent();

            ToolTipService.SetToolTip(AddPageSegment,    L.Subscription_AddTooltip);
            ToolTipService.SetToolTip(ManagePageSegment, L.Subscription_ManageTooltip);
        }

        private void RefreshButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                ToolTipService.SetToolTip(element, L.Subscription_Refresh);
        }

        private void EditButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                ToolTipService.SetToolTip(element, L.Subscription_EditTooltip);
        }

        // The edit flyout lives inside a DataTemplate, so x:Name would not generate
        // code-behind fields; the controls are located by position instead. This is the
        // single place that knows the StackPanel layout: [0] URL box, [1] name box,
        // last child = save button.
        private static (TextBox? UrlBox, TextBox? NameBox, Button? SaveButton) GetEditControls(StackPanel panel)
        {
            var children = panel.Children;
            return (
                children.Count > 0 ? children[0] as TextBox : null,
                children.Count > 1 ? children[1] as TextBox : null,
                children.Count > 0 ? children[children.Count - 1] as Button : null);
        }

        private void EditFlyout_Opening(object sender, object e)
        {
            if (sender is not Flyout { Content: StackPanel panel } flyout ||
                flyout.Target?.DataContext is not SubscriptionEntry sub)
                return;

            panel.Tag = flyout;

            var (urlBox, nameBox, _) = GetEditControls(panel);
            if (urlBox != null) urlBox.Text = sub.Url;
            if (nameBox != null) nameBox.Text = sub.Name;
        }

        private void EditUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox { Parent: StackPanel panel } box) return;

            var (_, _, saveBtn) = GetEditControls(panel);
            if (saveBtn != null)
                saveBtn.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        }

        private async void ConfirmEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: SubscriptionEntry sub, Parent: StackPanel panel } btn)
                return;

            var (urlBox, nameBox, _) = GetEditControls(panel);
            if (urlBox == null || nameBox == null) return;

            var url = urlBox.Text.Trim();
            if (url.Length == 0) return;

            HideAncestorFlyout(btn);
            await ViewModel.CommitEditAsync(sub, url, nameBox.Text);
        }

        private void DeleteButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                ToolTipService.SetToolTip(element, L.Subscription_DeleteTooltip);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SubscriptionEntry sub })
                ViewModel.RefreshSubscriptionCommand.Execute(sub);
        }

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: SubscriptionEntry sub } btn)
            {
                HideAncestorFlyout(btn);
                ViewModel.DeleteSubscriptionCommand.Execute(sub);
            }
        }

        private static void HideAncestorFlyout(DependencyObject element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement { Tag: FlyoutBase flyout })
                {
                    flyout.Hide();
                    return;
                }

                if (current is FlyoutPresenter fp && fp.Parent is Popup popup)
                {
                    popup.IsOpen = false;
                    return;
                }
                current = VisualTreeHelper.GetParent(current);
            }
        }
    }
}
