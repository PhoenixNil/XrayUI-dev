using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using XrayUI.Models;

namespace XrayUI.ViewModels
{
    public partial class ManageSubscriptionsViewModel : ObservableObject
    {
        private readonly Func<SubscriptionEntry, Task> _onRefresh;
        private readonly Func<SubscriptionEntry, Task<bool>> _onDelete;
        private int _selectedIndex;
        private string _subscriptionUrl = string.Empty;
        private string _subscriptionName = string.Empty;

        public ObservableCollection<SubscriptionEntry> Subscriptions { get; }

        public ManageSubscriptionsViewModel(
            IEnumerable<SubscriptionEntry> source,
            Func<SubscriptionEntry, Task> onRefresh,
            Func<SubscriptionEntry, Task<bool>> onDelete)
        {
            _onRefresh = onRefresh;
            _onDelete  = onDelete;

            Subscriptions = new ObservableCollection<SubscriptionEntry>(source);
            Subscriptions.CollectionChanged += OnCollectionChanged;
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (SetProperty(ref _selectedIndex, value))
                {
                    OnPropertyChanged(nameof(IsAddPage));
                    OnPropertyChanged(nameof(IsManagePage));
                    OnPropertyChanged(nameof(AddPageVisibility));
                    OnPropertyChanged(nameof(ManagePageVisibility));
                    OnPropertyChanged(nameof(CanAddSubscription));
                    OnPropertyChanged(nameof(DialogTitle));
                }
            }
        }

        public string SubscriptionUrl
        {
            get => _subscriptionUrl;
            set
            {
                if (SetProperty(ref _subscriptionUrl, value))
                    OnPropertyChanged(nameof(CanAddSubscription));
            }
        }

        public string SubscriptionName
        {
            get => _subscriptionName;
            set => SetProperty(ref _subscriptionName, value);
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasSubscriptions));
            OnPropertyChanged(nameof(EmptyStateVisibility));
            OnPropertyChanged(nameof(ListVisibility));
        }

        public bool HasSubscriptions => Subscriptions.Count > 0;

        public bool IsAddPage => SelectedIndex == 0;
        public bool IsManagePage => SelectedIndex == 1;
        public bool CanAddSubscription => IsAddPage && !string.IsNullOrWhiteSpace(SubscriptionUrl);
        public string DialogTitle => IsAddPage ? "添加订阅" : "管理订阅";

        public Visibility AddPageVisibility => IsAddPage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ManagePageVisibility => IsManagePage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyStateVisibility => HasSubscriptions ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ListVisibility       => HasSubscriptions ? Visibility.Visible : Visibility.Collapsed;

        public SubscriptionEntry? CreateSubscription()
        {
            var url = SubscriptionUrl.Trim();
            if (string.IsNullOrEmpty(url)) return null;

            var name = string.IsNullOrWhiteSpace(SubscriptionName)
                ? TryGetHost(url)
                : SubscriptionName.Trim();

            return new SubscriptionEntry { Url = url, Name = name };
        }

        [RelayCommand]
        private Task RefreshSubscription(SubscriptionEntry sub) => _onRefresh(sub);

        [RelayCommand]
        private async Task DeleteSubscription(SubscriptionEntry sub)
        {
            var ok = await _onDelete(sub);
            if (ok) Subscriptions.Remove(sub);
        }

        private static string TryGetHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }
    }
}
