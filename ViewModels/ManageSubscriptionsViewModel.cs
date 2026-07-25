using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.ViewModels
{
    public partial class ManageSubscriptionsViewModel : ObservableObject
    {
        // Bulk-refresh fan-out. Each subscription costs two requests plus a settings write and a list
        // rebuild, so this stays low enough to avoid tripping provider rate limits.
        private const int MaxConcurrentRefresh = 3;

        private readonly Func<SubscriptionEntry, Task> _onRefresh;
        private readonly Func<SubscriptionEntry, Task<bool>> _onDelete;
        private readonly Func<SubscriptionEntry, Task> _onEdit;

        public ObservableCollection<SubscriptionEntry> Subscriptions { get; }

        public ManageSubscriptionsViewModel(
            IEnumerable<SubscriptionEntry> source,
            Func<SubscriptionEntry, Task> onRefresh,
            Func<SubscriptionEntry, Task<bool>> onDelete,
            Func<SubscriptionEntry, Task> onEdit)
        {
            _onRefresh = onRefresh;
            _onDelete  = onDelete;
            _onEdit    = onEdit;

            Subscriptions = new ObservableCollection<SubscriptionEntry>(source);
            Subscriptions.CollectionChanged += OnCollectionChanged;
            SubscriptionUrl = string.Empty;
            SubscriptionName = string.Empty;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAddPage))]
        [NotifyPropertyChangedFor(nameof(IsManagePage))]
        [NotifyPropertyChangedFor(nameof(AddPageVisibility))]
        [NotifyPropertyChangedFor(nameof(ManagePageVisibility))]
        [NotifyPropertyChangedFor(nameof(CanAddSubscription))]
        [NotifyPropertyChangedFor(nameof(DialogTitle))]
        public partial int SelectedIndex { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAddSubscription))]
        public partial string SubscriptionUrl { get; set; }

        [ObservableProperty]
        public partial string SubscriptionName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RefreshAllText))]
        public partial bool IsRefreshingAll { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RefreshAllText))]
        public partial int RefreshAllDone { get; set; }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSubscriptions));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(ListVisibility));
            OnPropertyChanged(nameof(SubscriptionCountText));
        }

        public bool HasSubscriptions => Subscriptions.Count > 0;

        public bool IsAddPage => SelectedIndex == 0;
        public bool IsManagePage => SelectedIndex == 1;
        public bool CanAddSubscription => IsAddPage && !string.IsNullOrWhiteSpace(SubscriptionUrl);
        public string DialogTitle => IsAddPage ? L.Subscription_DialogTitle_Add : L.Subscription_DialogTitle_Manage;

        public Visibility AddPageVisibility => IsAddPage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ManagePageVisibility => IsManagePage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyStateVisibility => HasSubscriptions ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ListVisibility       => HasSubscriptions ? Visibility.Visible : Visibility.Collapsed;

        public string SubscriptionCountText => Loc.Format("Subscription_Count", Subscriptions.Count);

        /// <summary>Idle shows the action label; mid-run it doubles as the progress readout.</summary>
        public string RefreshAllText => IsRefreshingAll
            ? Loc.Format("Subscription_UpdatingAll", RefreshAllDone, Subscriptions.Count)
            : L.Subscription_UpdateAll;

        public SubscriptionEntry? CreateSubscription()
        {
            var url = SubscriptionUrl.Trim();
            if (string.IsNullOrEmpty(url)) return null;

            return new SubscriptionEntry { Url = url, Name = ResolveName(SubscriptionName, url) };
        }

        [RelayCommand]
        private Task RefreshSubscription(SubscriptionEntry sub) => _onRefresh(sub);

        /// <summary>
        /// Refreshes every subscription, at most <see cref="MaxConcurrentRefresh"/> at a time. Capped
        /// rather than fully parallel: each refresh issues two requests and ends in a settings/server
        /// write plus a list rebuild, so an unbounded sweep would spike request count and pile up
        /// concurrent rebuilds. Individual failures land in that entry's LastError (handled inside the
        /// refresh callback), so the sweep never aborts early.
        /// </summary>
        [RelayCommand]
        private async Task RefreshAll()
        {
            if (IsRefreshingAll || Subscriptions.Count == 0) return;

            IsRefreshingAll = true;
            RefreshAllDone  = 0;
            try
            {
                using var gate = new SemaphoreSlim(MaxConcurrentRefresh);
                var tasks = Subscriptions.ToList().Select(async sub =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        await _onRefresh(sub);
                    }
                    finally
                    {
                        gate.Release();
                        // Continuations resume on the UI thread (no ConfigureAwait above), so the
                        // tally needs no synchronization of its own.
                        RefreshAllDone++;
                    }
                });

                await Task.WhenAll(tasks);
            }
            finally
            {
                IsRefreshingAll = false;
            }
        }

        public async Task CommitEditAsync(SubscriptionEntry sub, string url, string name)
        {
            var oldUrl   = sub.Url;
            var oldName  = sub.Name;
            var oldUsage = sub.Usage;
            var urlChanged = !string.Equals(sub.Url, url, StringComparison.Ordinal);
            var editSaved = false;

            sub.Url  = url;
            sub.Name = ResolveName(name, url);
            if (urlChanged)
            {
                // A different link is a different account, so the old quota/expiry no longer describes it.
                sub.LastError = null;
                sub.Usage     = default;
            }

            try
            {
                await _onEdit(sub);
                editSaved = true;
                if (urlChanged) await _onRefresh(sub);
            }
            catch (Exception ex)
            {
                if (!editSaved)
                {
                    sub.Url   = oldUrl;
                    sub.Name  = oldName;
                    sub.Usage = oldUsage;
                }

                // Fetch failures are handled inside the refresh callback and land in
                // LastError there; what escapes to here is persistence I/O (settings /
                // server-list writes). Surface it on the card instead of losing it.
                sub.LastError = Loc.Format("Subscription_UpdateFailed", ex.Message);
            }
        }

        [RelayCommand]
        private async Task DeleteSubscription(SubscriptionEntry sub)
        {
            var ok = await _onDelete(sub);
            if (ok) Subscriptions.Remove(sub);
        }

        /// <summary>Empty name falls back to the link's host, mirroring the add page.</summary>
        private static string ResolveName(string name, string url) =>
            string.IsNullOrWhiteSpace(name) ? TryGetHost(url) : name.Trim();

        private static string TryGetHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }
    }
}
