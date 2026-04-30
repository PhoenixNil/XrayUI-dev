using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class ServerListControl
    {
        public ServerListViewModel ViewModel { get; set; } = null!;
        public IAsyncRelayCommand? SwitchToSelectedServerCommand { get; set; }

        public ServerListControl()
        {
            this.InitializeComponent();
        }

        private void ServerSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;

            var query = sender.Text.Trim();
            ViewModel.SearchQuery = query;

            if (string.IsNullOrEmpty(query))
            {
                sender.ItemsSource = null;
                return;
            }

            sender.ItemsSource = ViewModel.SearchServers(query);
        }

        private void ServerSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is ServerEntry server)
            {
                ViewModel.SelectedServer = server;
                sender.Text = server.Name;
            }
        }

        private void ServerSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is ServerEntry chosenServer)
            {
                ViewModel.SelectedServer = chosenServer;
                return;
            }

            var query = args.QueryText?.Trim();
            if (string.IsNullOrEmpty(query)) return;

            var match = ViewModel.Servers.FirstOrDefault(s =>
                string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase));

            match ??= ViewModel.Servers.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.Name) &&
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                ViewModel.SelectedServer = match;
                sender.Text = match.Name;
            }
        }

        private async void ServersListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await ViewModel.SaveOrderAsync();
        }

        private async void ServerItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            if (element.DataContext is not ServerEntry server)
                return;

            if (!ReferenceEquals(ViewModel.SelectedServer, server))
                ViewModel.SelectedServer = server;

            var command = SwitchToSelectedServerCommand;
            if (command is null || !command.CanExecute(null))
                return;

            e.Handled = true;
            await command.ExecuteAsync(null);
        }

        private void ServerItem_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                e.Handled = true;
                return;
            }

            if (!ReferenceEquals(element.DataContext, ViewModel.SelectedServer))
            {
                e.Handled = true;
                return;
            }

            var flyout = CreateSelectedServerContextFlyout();

            if (e.TryGetPosition(element, out Point point))
            {
                flyout.ShowAt(element, new FlyoutShowOptions
                {
                    Position = point
                });
            }
            else
            {
                flyout.ShowAt(element);
            }

            e.Handled = true;
        }

        private MenuFlyout CreateSelectedServerContextFlyout()
        {
            var flyout = new MenuFlyout();

            var editItem = CreateMenuItem("编辑", "\uE70F");
            editItem.IsEnabled = ViewModel.CanEditSelectedServer;
            editItem.Click += (_, _) => ViewModel.EditServerCommand.Execute(null);

            var deleteItem = CreateMenuItem("删除", "\uE74D");
            deleteItem.IsEnabled = ViewModel.CanRemoveSelectedServer;
            deleteItem.Click += (_, _) => ViewModel.RemoveServerCommand.Execute(null);

            var shareItem = CreateMenuItem("分享", "\uE72D");
            shareItem.Click += (_, _) => ViewModel.ShareServerCommand.Execute(null);

            flyout.Items.Add(editItem);
            flyout.Items.Add(deleteItem);
            flyout.Items.Add(shareItem);

            return flyout;
        }

        private static MenuFlyoutItem CreateMenuItem(string text, string glyph)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph }
            };
        }
    }
}
