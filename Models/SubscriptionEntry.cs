using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using XrayUI.Helpers;

namespace XrayUI.Models
{
    /// <summary>
    /// Traffic + expiry as reported by a provider's <c>subscription-userinfo</c> response header.
    /// Kept as one value so the fetch, the model and the edit-rollback path all move the whole set
    /// instead of enumerating four fields each — the failure mode when they diverge is a figure that
    /// silently stops persisting.
    /// </summary>
    public readonly record struct SubscriptionUserInfo(long? Up, long? Down, long? Total, DateTimeOffset? Expire);

    public class SubscriptionEntry : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = string.Empty;
        private string _url = string.Empty;
        private DateTimeOffset? _lastUpdated;
        private string? _lastError;
        private bool _isBusy;
        private long? _upload;
        private long? _download;
        private long? _total;
        private DateTimeOffset? _expire;
        private int _autoRefreshIntervalMinutes;
        private DateTimeOffset? _lastRefreshAttempt;

        public string Id
        {
            get => _id;
            set { _id = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Url
        {
            get => _url;
            set { _url = value; OnPropertyChanged(); }
        }

        public DateTimeOffset? LastUpdated
        {
            get => _lastUpdated;
            set
            {
                _lastUpdated = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastUpdatedText));
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }

        public string? LastError
        {
            get => _lastError;
            set
            {
                _lastError = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(LastErrorText));
            }
        }

        [JsonIgnore]
        public string LastUpdatedText
        {
            get
            {
                if (!_lastUpdated.HasValue) return L.Subscription_NeverUpdated;
                var delta = DateTimeOffset.Now - _lastUpdated.Value;
                string rel;
                if (delta.TotalSeconds < 60)      rel = L.Subscription_JustNow;
                else if (delta.TotalMinutes < 60) rel = Loc.Format("Subscription_MinutesAgo", (int)delta.TotalMinutes);
                else if (delta.TotalHours   < 24) rel = Loc.Format("Subscription_HoursAgo",   (int)delta.TotalHours);
                else if (delta.TotalDays    < 30) rel = Loc.Format("Subscription_DaysAgo",    (int)delta.TotalDays);
                else                              rel = _lastUpdated.Value.LocalDateTime.ToString("yyyy-MM-dd");
                return Loc.Format("Subscription_LastUpdated", rel);
            }
        }

        [JsonIgnore]
        public string LastErrorText => _lastError ?? string.Empty;

        /// <summary>
        /// Per-subscription automatic refresh interval. Zero disables scheduling; all other values
        /// are normalized to the fixed choices in <see cref="SubscriptionRefreshSchedule"/>.
        /// </summary>
        public int AutoRefreshIntervalMinutes
        {
            get => _autoRefreshIntervalMinutes;
            set
            {
                var normalized = SubscriptionRefreshSchedule.NormalizeInterval(value);
                if (_autoRefreshIntervalMinutes == normalized) return;

                _autoRefreshIntervalMinutes = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAutoRefreshEnabled));
                OnPropertyChanged(nameof(AutoRefreshSummaryText));
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }

        /// <summary>
        /// Most recent manual, bulk or scheduled refresh attempt. Failures count so an unavailable
        /// provider is not hammered every scheduler tick.
        /// </summary>
        public DateTimeOffset? LastRefreshAttempt
        {
            get => _lastRefreshAttempt;
            set
            {
                if (_lastRefreshAttempt == value) return;
                _lastRefreshAttempt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpdateStatusText));
            }
        }

        [JsonIgnore]
        public bool IsAutoRefreshEnabled => _autoRefreshIntervalMinutes > 0;

        [JsonIgnore]
        public string AutoRefreshSummaryText => IsAutoRefreshEnabled
            ? Loc.Format("Subscription_AutoRefreshSummary", _autoRefreshIntervalMinutes / 60)
            : string.Empty;

        /// <summary>
        /// The card's third line. A subscription that refreshes itself shows its cadence instead of
        /// a "last updated" stamp: with a schedule running and no error, the last success is bounded
        /// by the interval anyway, so the cadence is the more useful of the two — and it makes the
        /// scheduled ones scannable down the list without costing a row. An overdue schedule falls
        /// back to the stamp, which is the one case where the two disagree (app closed for longer
        /// than the interval); there the honest answer is how old the data actually is.
        /// Reads the clock, so it is not notification-driven past the three setters that raise it —
        /// same as <see cref="LastUpdatedText"/>, and fine for a modal that is open briefly.
        /// </summary>
        [JsonIgnore]
        public string UpdateStatusText =>
            IsAutoRefreshEnabled &&
            !SubscriptionRefreshSchedule.IsDue(_autoRefreshIntervalMinutes, _lastRefreshAttempt, DateTimeOffset.UtcNow)
                ? AutoRefreshSummaryText
                : LastUpdatedText;

        // Traffic + expiry parsed from the provider's `subscription-userinfo` response header
        // (bytes / unix-seconds). Optional — many self-built or converter subscriptions omit it,
        // in which case the card falls back to showing the URL. Persisted so the figures survive
        // a restart without a re-fetch. The individual properties exist for the JSON round trip;
        // code holding the whole set assigns Usage instead.
        public long? Upload
        {
            get => _upload;
            set => Usage = Usage with { Up = value };
        }

        public long? Download
        {
            get => _download;
            set => Usage = Usage with { Down = value };
        }

        public long? Total
        {
            get => _total;
            set => Usage = Usage with { Total = value };
        }

        public DateTimeOffset? Expire
        {
            get => _expire;
            set => Usage = Usage with { Expire = value };
        }

        /// <summary>
        /// All four userinfo figures as one value. Assigning writes them together and raises the
        /// derived notifications once, so the common case — a refresh reporting an unchanged quota —
        /// costs nothing instead of re-rendering the card four times.
        /// </summary>
        [JsonIgnore]
        public SubscriptionUserInfo Usage
        {
            get => new(_upload, _download, _total, _expire);
            set
            {
                if (value == Usage) return;

                (_upload, _download, _total, _expire) = (value.Up, value.Down, value.Total, value.Expire);
                OnPropertyChanged(nameof(Upload));
                OnPropertyChanged(nameof(Download));
                OnPropertyChanged(nameof(Total));
                OnPropertyChanged(nameof(Expire));
                OnPropertyChanged(nameof(HasTraffic));
                OnPropertyChanged(nameof(TrafficText));
                OnPropertyChanged(nameof(HasExpiry));
                OnPropertyChanged(nameof(ExpiryText));
                OnPropertyChanged(nameof(TrafficLineText));
            }
        }

        // Traffic requires a known positive quota; total == 0 means "unlimited" and has no bar to show.
        [JsonIgnore] public bool HasTraffic => _total.HasValue && _total.Value > 0;

        [JsonIgnore]
        public string TrafficText =>
            HasTraffic ? $"{FormatBytes((_upload ?? 0) + (_download ?? 0))} / {FormatBytes(_total!.Value)}" : string.Empty;

        [JsonIgnore] public bool HasExpiry => _expire.HasValue;

        [JsonIgnore]
        public string ExpiryText =>
            _expire.HasValue ? Loc.Format("Subscription_Expires", _expire.Value.LocalDateTime.ToString("yyyy-MM-dd")) : string.Empty;

        // The card's second line, e.g. "6.21GB / 100GB · Expires 2026-08-01". Composed here rather
        // than from nested XAML panels because Run.Text is not a dependency property in WinUI, so
        // the alternative is a StackPanel per fragment with its own visibility binding.
        [JsonIgnore]
        public string TrafficLineText => HasExpiry ? $"{TrafficText} · {ExpiryText}" : TrafficText;

        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

        // Human-readable bytes matching the Clash Verge convention: base-1024 units with no space
        // before the unit, ~3 significant figures (6.21GB, 60.0GB, 341MB, 580KB, 100GB).
        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < ByteUnits.Length - 1) { value /= 1024; unit++; }
            string number = value >= 100 ? value.ToString("0")
                          : value >= 10  ? value.ToString("0.0")
                          :                value.ToString("0.00");
            return number + ByteUnits[unit];
        }

        [JsonIgnore]
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }

        [JsonIgnore] public bool IsNotBusy => !_isBusy;
        [JsonIgnore] public bool HasError  => !string.IsNullOrEmpty(_lastError);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
