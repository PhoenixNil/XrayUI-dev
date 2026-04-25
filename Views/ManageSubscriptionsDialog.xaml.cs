using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using XrayUI.Models;
using XrayUI.ViewModels;

namespace XrayUI.Views
{
    public sealed partial class ManageSubscriptionsDialog : UserControl
    {
        public ManageSubscriptionsViewModel ViewModel { get; }

        public ManageSubscriptionsDialog(ManageSubscriptionsViewModel vm)
        {
            ViewModel = vm;
            InitializeComponent();
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
