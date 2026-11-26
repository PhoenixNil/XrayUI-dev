using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XrayUI.Helpers;
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

            // Localize attached properties that x:Uid does not address cleanly.
            AutomationProperties.SetName(FilterToggle, L.ServerList_FilterTooltip);
            AutomationProperties.SetName(TestLatencyButton, L.ServerList_TestLatencyTooltip);
            AutomationProperties.SetName(SortButton,   L.ServerList_SortTooltip);
            ToolTipService.SetToolTip(FilterToggle, L.ServerList_FilterTooltip);
            ToolTipService.SetToolTip(TestLatencyButton, L.ServerList_TestLatencyTooltip);
            ToolTipService.SetToolTip(SortActiveItem,  L.ServerList_SortActiveHint);
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

        private bool _initialScrollDone;

        private void ServersListView_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RevealSelectionRequested -= OnRevealSelectionRequested;
            ViewModel.RevealSelectionRequested += OnRevealSelectionRequested;
            Unloaded -= OnControlUnloaded;
            Unloaded += OnControlUnloaded;
            QueueInitialScroll();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e) =>
            ViewModel.RevealSelectionRequested -= OnRevealSelectionRequested;

        private void OnRevealSelectionRequested(object? sender, EventArgs e)
        {
            _initialScrollDone = false;
            QueueInitialScroll();
        }

        private void ServersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            QueueInitialScroll();
            ViewModel.SetSelectedServers(ServersListView.SelectedItems.OfType<ServerEntry>().ToArray());
        }

        private void QueueInitialScroll()
        {
            if (_initialScrollDone || ServersListView.SelectedItem is null)
                return;

            ServersListView.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    // Startup can restore the selection before the virtualized list has
                    // completed its first layout pass (more likely with Native AOT's
                    // faster startup), where ScrollIntoView silently no-ops. Bail without
                    // consuming the one-time scroll until the control is loaded and the
                    // current item is known — the next Loaded/SelectionChanged retries.
                    if (_initialScrollDone ||
                        !ServersListView.IsLoaded ||
                        ServersListView.SelectedItem is not { } selectedItem)
                    {
                        return;
                    }

                    ServersListView.UpdateLayout();
                    ServersListView.ScrollIntoView(
                        selectedItem,
                        ScrollIntoViewAlignment.Leading);
                    _initialScrollDone = true;
                });
        }

        private void ActiveBadge_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is UIElement element)
                element.CenterPoint = new Vector3((float)e.NewSize.Width / 2f, (float)e.NewSize.Height / 2f, 0f);
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
            if (ViewModel.HasMultipleSelectedServers)
            {
                e.Handled = true;
                return;
            }

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

            var server = ViewModel.SelectedServer;

            var testLatencyItem = CreateMenuItem(L.ServerList_TestNodeLatency, "\uF42F");
            testLatencyItem.IsEnabled = server is not null && ViewModel.CanTestServerLatency(server);
            testLatencyItem.Click += (_, _) => ViewModel.TestServerLatencyCommand.Execute(server);

            var isFavorite = server?.IsFavorite == true;
            var favoriteItem = CreateMenuItem(
                isFavorite ? L.ServerList_RemoveFavorite : L.ServerList_AddFavorite,
                isFavorite ? "\uE8D9" : "\uE734");
            favoriteItem.Click += (_, _) => ViewModel.ToggleFavoriteCommand.Execute(null);

            var editItem = CreateMenuItem(L.ServerList_Edit, "\uE70F");
            editItem.IsEnabled = ViewModel.CanEditSelectedServer;
            editItem.Click += (_, _) => ViewModel.EditServerCommand.Execute(null);

            var shareItem = CreateMenuItem(L.ServerList_Share, "\uE72D");
            shareItem.Click += (_, _) => ViewModel.ShareServerCommand.Execute(null);

            var deleteItem = CreateMenuItem(L.ServerList_Delete, "\uE74D");
            deleteItem.IsEnabled = ViewModel.CanRemoveSelectedServer;
            deleteItem.Click += (_, _) => ViewModel.RemoveServerCommand.Execute(null);

            flyout.Items.Add(testLatencyItem);
            flyout.Items.Add(favoriteItem);
            flyout.Items.Add(editItem);
            flyout.Items.Add(shareItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(deleteItem);

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

        // Right-click latency-test mode menu pushes the chosen mode into the VM; the toolbar icon
        // follows automatically via x:Bind on LatencyTestMode (see *IconVisibility below).
        private void TestModeConnectItem_Click(object sender, RoutedEventArgs e)
            => ViewModel.LatencyTestMode = "connect";

        private void TestModeRealItem_Click(object sender, RoutedEventArgs e)
            => ViewModel.LatencyTestMode = "real";

        // Toolbar icon visibility derived from the latency-test mode (bound from XAML).
        public static Visibility ConnectModeIconVisibility(string mode)
            => mode == "real" ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility RealModeIconVisibility(string mode)
            => mode == "real" ? Visibility.Visible : Visibility.Collapsed;

        // The row's latency number gives way to its spinner while a single-node test runs.
        public static Visibility LatencyTextVisibility(bool hasLatency, bool isTesting)
            => hasLatency && !isTesting ? Visibility.Visible : Visibility.Collapsed;

        // Accessible name for the row spinner. Cached: the item template's x:Bind reads it on
        // every row realization, Mode=OneTime or not.
        public static string TestingLatencyName { get; } = L.ServerList_TestingNodeLatency;

        public static double ActiveBadgeOpacity(bool isActive)
            => isActive ? 1.0 : 0.0;

        public static Vector3 ActiveBadgeScale(bool isActive)
            => isActive ? Vector3.One : new Vector3(0.92f, 0.92f, 1f);

        // Foreground for the per-row latency number, keyed off the measured value:
        // failed probe (negative, e.g. -1) → critical, ≥200 ms → caution, else success.
        public static Brush LatencyForeground(int? milliseconds)
        {
            var key = milliseconds switch
            {
                < 0   => "LatencyFailBrush",
                < 200 => "LatencyGoodBrush",
                _     => "LatencyHighBrush",
            };
            return (Brush)Application.Current.Resources[key];
        }
    }
}
