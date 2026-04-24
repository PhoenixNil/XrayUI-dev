using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class ServerListViewModel : ObservableObject, IDisposable
    {
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly IDialogService  _dialogs;
        private readonly SettingsService _settings;
        private readonly SemaphoreSlim   _settingsWriteLock = new(1, 1);
        private ObservableCollection<ServerEntry> _servers = new();
        private ServerEntry? _selectedServer;
        private bool _isProxyRunning;
        private bool _disposed;

        public ServerListViewModel(IDialogService dialogs, SettingsService settings)
        {
            _dialogs  = dialogs;
            _settings = settings;

            ProtocolColorStore.ColorsChanged += OnProtocolColorsChanged;
        }

        private void OnProtocolColorsChanged(object? sender, EventArgs e)
        {
            foreach (var s in Servers)
                s.RefreshProtocolColor();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ProtocolColorStore.ColorsChanged -= OnProtocolColorsChanged;
        }

        public ObservableCollection<ServerEntry> Servers
        {
            get => _servers;
            set => SetProperty(ref _servers, value);
        }

        public ServerEntry? SelectedServer
        {
            get => _selectedServer;
            set
            {
                var previous = _selectedServer;
                if (SetProperty(ref _selectedServer, value))
                {
                    OnSelectedServerChanged(previous, value);
                }
            }
        }

        public bool IsProxyRunning
        {
            get => _isProxyRunning;
            set
            {
                if (SetProperty(ref _isProxyRunning, value))
                {
                    NotifyServerActionStateChanged();
                }
            }
        }

        public bool IsSelectedServerLocked => IsProxyRunning && SelectedServer?.IsActive == true;

        public bool CanEditSelectedServer => SelectedServer != null && !IsSelectedServerLocked;

        public bool CanRemoveSelectedServer => SelectedServer != null && !IsSelectedServerLocked;

        public bool CanEditServer => CanEditSelectedServer;

        public bool CanRemoveServer => CanRemoveSelectedServer;

        // ── Search ────────────────────────────────────────────────────────────

        private const int MaxSearchSuggestions = 20;

        public IReadOnlyList<ServerEntry> SearchServers(string query)
        {
            return Servers
                .Where(s => !string.IsNullOrEmpty(s.Name) &&
                            s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchSuggestions)
                .ToArray();
        }

        // ── Persistence ───────────────────────────────────────────────────────

        public async Task LoadServersAsync()
        {
            var list = await _settings.LoadServersAsync();
            foreach (var s in list)
                Servers.Add(s);

            if (Servers.Count > 0 && SelectedServer == null)
                SelectedServer = Servers[0];
        }

        private Task SaveAsync() => _settings.SaveServersAsync(Servers);

        public Task SaveOrderAsync() => SaveAsync();

        private void OnSelectedServerChanged(ServerEntry? previous, ServerEntry? current)
        {
            if (previous is not null)
            {
                previous.PropertyChanged -= OnSelectedServerPropertyChanged;
            }

            if (current is not null)
            {
                current.PropertyChanged += OnSelectedServerPropertyChanged;
            }

            NotifyServerActionStateChanged();
        }

        private void OnSelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServerEntry.IsActive))
            {
                NotifyServerActionStateChanged();
            }
        }

        private void NotifyServerActionStateChanged()
        {
            OnPropertyChanged(nameof(IsSelectedServerLocked));
            OnPropertyChanged(nameof(CanEditSelectedServer));
            OnPropertyChanged(nameof(CanRemoveSelectedServer));
            OnPropertyChanged(nameof(CanEditServer));
            OnPropertyChanged(nameof(CanRemoveServer));
        }

        // ── Import via link ───────────────────────────────────────────────────

        [RelayCommand]
        private async Task ImportFromLink()
        {
            var text = await _dialogs.ShowImportLinkDialogAsync();
            if (text == null) return;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            ServerEntry? lastAdded = null;

            foreach (var line in lines)
            {
                var entry = NodeLinkParser.Parse(line.Trim());
                if (entry == null) continue;

                Servers.Add(entry);
                lastAdded = entry;
                added++;
            }

            if (added == 0)
            {
                await _dialogs.ShowErrorAsync("解析失败", "无法识别有效的节点链接，请检查后重试。");
                return;
            }

            SelectedServer = lastAdded;
            await SaveAsync();
        }

        // ── Subscriptions ─────────────────────────────────────────────────────

        [RelayCommand]
        private async Task OpenSubscriptions()
        {
            var settings = await _settings.LoadSettingsAsync();
            var vm = new ManageSubscriptionsViewModel(
                settings.Subscriptions ?? new List<SubscriptionEntry>(),
                RefreshSubscriptionAsync,
                DeleteSubscriptionAsync);

            var sub = await _dialogs.ShowSubscriptionsDialogAsync(vm);
            if (sub == null) return;

            sub.Id = Guid.NewGuid().ToString("N");

            var (entries, error) = await FetchSubscriptionNodesAsync(sub);

            if (entries != null)
            {
                foreach (var e in entries) Servers.Add(e);
                sub.LastUpdated = DateTimeOffset.Now;
                sub.LastError   = null;
                if (SelectedServer == null && Servers.Count > 0)
                    SelectedServer = Servers[^1];
            }
            else
            {
                sub.LastError = error;
            }

            await UpsertSubscriptionAsync(sub);
            await SaveAsync();

            if (entries == null)
            {
                await _dialogs.ShowErrorAsync("订阅拉取失败", error ?? "未知错误");
            }
        }

        private static async Task<(List<ServerEntry>? entries, string? error)> FetchSubscriptionNodesAsync(SubscriptionEntry sub)
        {
            string raw;
            try
            {
                raw = await Http.GetStringAsync(sub.Url);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }

            var trimmed = raw.Trim();
            var decoded = new byte[trimmed.Length];
            var text = Convert.TryFromBase64String(trimmed, decoded, out var written)
                ? Encoding.UTF8.GetString(decoded, 0, written)
                : raw;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<ServerEntry>();
            foreach (var line in lines)
            {
                var entry = NodeLinkParser.Parse(line.Trim());
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.Name))
                    entry.Name = $"{sub.Name} #{entries.Count + 1}";
                entry.SubscriptionId = sub.Id;
                entries.Add(entry);
            }

            if (entries.Count == 0)
                return (null, "未能从订阅中解析出任何有效节点。");

            return (entries, null);
        }

        private async Task RefreshSubscriptionAsync(SubscriptionEntry sub)
        {
            if (IsSubscriptionLocked(sub.Id))
            {
                sub.LastError = "请先停止代理后再刷新";
                return;
            }

            sub.IsBusy = true;
            try
            {
                var (newEntries, error) = await FetchSubscriptionNodesAsync(sub);
                if (newEntries == null)
                {
                    sub.LastError = $"更新失败: {error}";
                    return;
                }

                var removed = Servers.Where(s => s.SubscriptionId == sub.Id).ToList();
                var wasSelectedId = SelectedServer?.Id;

                // Preserve Ids for nodes that survived the refresh so LastAutoConnectServerId
                // (and any other Id-based reference) keeps pointing at the same logical node.
                var oldByEndpoint = new Dictionary<string, ServerEntry>(removed.Count);
                foreach (var s in removed)
                    oldByEndpoint[$"{s.Protocol}://{s.Host}:{s.Port}"] = s;
                foreach (var e in newEntries)
                {
                    if (oldByEndpoint.TryGetValue($"{e.Protocol}://{e.Host}:{e.Port}", out var match))
                        e.Id = match.Id;
                }

                foreach (var s in removed) Servers.Remove(s);
                foreach (var e in newEntries) Servers.Add(e);

                if (wasSelectedId != null && Servers.All(s => s.Id != wasSelectedId))
                    SelectedServer = newEntries.FirstOrDefault() ?? Servers.FirstOrDefault();

                sub.LastUpdated = DateTimeOffset.Now;
                sub.LastError   = null;

                await SaveAsync();
            }
            finally
            {
                sub.IsBusy = false;
                await UpsertSubscriptionAsync(sub);
            }
        }

        private async Task<bool> DeleteSubscriptionAsync(SubscriptionEntry sub)
        {
            if (IsSubscriptionLocked(sub.Id))
            {
                sub.LastError = "请先停止代理后再删除";
                return false;
            }

            var removed = Servers.Where(s => s.SubscriptionId == sub.Id).ToList();
            foreach (var s in removed) Servers.Remove(s);

            if (SelectedServer != null && !Servers.Contains(SelectedServer))
                SelectedServer = Servers.FirstOrDefault();

            await RemoveSubscriptionAsync(sub.Id);
            await SaveAsync();
            return true;
        }

        private bool IsSubscriptionLocked(string subscriptionId)
        {
            return IsProxyRunning && Servers.Any(s =>
                s.IsActive &&
                string.Equals(s.SubscriptionId, subscriptionId, StringComparison.Ordinal));
        }

        private async Task UpsertSubscriptionAsync(SubscriptionEntry sub)
        {
            await _settingsWriteLock.WaitAsync();
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                settings.Subscriptions ??= new List<SubscriptionEntry>();

                var idx = settings.Subscriptions.FindIndex(s => s.Id == sub.Id);
                var snapshot = CloneForPersistence(sub);
                if (idx >= 0) settings.Subscriptions[idx] = snapshot;
                else          settings.Subscriptions.Add(snapshot);

                await _settings.SaveSettingsAsync(settings);
            }
            finally
            {
                _settingsWriteLock.Release();
            }
        }

        private async Task RemoveSubscriptionAsync(string subId)
        {
            await _settingsWriteLock.WaitAsync();
            try
            {
                var settings = await _settings.LoadSettingsAsync();
                if (settings.Subscriptions == null) return;
                settings.Subscriptions.RemoveAll(s => s.Id == subId);
                if (settings.Subscriptions.Count == 0) settings.Subscriptions = null;
                await _settings.SaveSettingsAsync(settings);
            }
            finally
            {
                _settingsWriteLock.Release();
            }
        }

        private static SubscriptionEntry CloneForPersistence(SubscriptionEntry sub) => new()
        {
            Id          = sub.Id,
            Name        = sub.Name,
            Url         = sub.Url,
            LastUpdated = sub.LastUpdated,
            LastError   = sub.LastError,
        };

        // ── Add manual ────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task AddManual()
        {
            var entry = await _dialogs.ShowEditServerDialogAsync(null);
            if (entry == null) return;

            Servers.Add(entry);
            SelectedServer = entry;
            await SaveAsync();
        }

        // ── Edit ──────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task EditServer()
        {
            if (SelectedServer is null) return;

            // Pass existing so dialog can pre-populate; dialog mutates and returns same ref on Primary
            var result = await _dialogs.ShowEditServerDialogAsync(SelectedServer);
            if (result == null) return;

            // result is the same object (mutated in-place by DialogService)
            await SaveAsync();
        }

        // ── Share ─────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task ShareServer()
        {
            if (SelectedServer is null) return;

            var link = NodeLinkSerializer.ToLink(SelectedServer);
            if (string.IsNullOrEmpty(link))
            {
                await _dialogs.ShowErrorAsync("不支持分享", "该节点协议暂不支持生成分享链接。");
                return;
            }

            await _dialogs.ShowShareLinkDialogAsync(SelectedServer.Name, link);
        }

        // ── Remove ────────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task RemoveServer()
        {
            if (SelectedServer is null) return;

            var confirmed = await _dialogs.ShowConfirmationAsync(
                "确认删除",
                $"确定要删除 {SelectedServer.Name}?",
                "删除",
                "取消",
                isDanger: true);
            if (!confirmed) return;

            var toRemove = SelectedServer;
            var idx      = Servers.IndexOf(toRemove);
            Servers.Remove(toRemove);

            SelectedServer = Servers.Count > 0
                ? Servers[System.Math.Max(0, idx - 1)]
                : null;

            await SaveAsync();
        }
    }
}
