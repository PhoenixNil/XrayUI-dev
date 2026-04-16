using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class ServerListViewModel : ObservableObject
    {
        private readonly IDialogService  _dialogs;
        private readonly SettingsService _settings;
        private ObservableCollection<ServerEntry> _servers = new();
        private ServerEntry? _selectedServer;
        private bool _isProxyRunning;

        public ServerListViewModel(IDialogService dialogs, SettingsService settings)
        {
            _dialogs  = dialogs;
            _settings = settings;
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

        // ── Add subscription ──────────────────────────────────────────────────

        [RelayCommand]
        private async Task AddSubscription()
        {
            var sub = await _dialogs.ShowAddSubscriptionDialogAsync();
            if (sub == null) return;

            string raw;
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                raw = await http.GetStringAsync(sub.Url);
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync("订阅拉取失败", ex.Message);
                return;
            }

            // 尝试 Base64 解码（标准订阅格式）
            string text = raw;
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Trim()));
            }
            catch { /* 不是 base64，原文使用 */ }

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            foreach (var line in lines)
            {
                var entry = NodeLinkParser.Parse(line.Trim());
                if (entry == null) continue;
                if (string.IsNullOrEmpty(entry.Name))
                    entry.Name = $"{sub.Name} #{added + 1}";
                Servers.Add(entry);
                added++;
            }

            if (added == 0)
            {
                await _dialogs.ShowErrorAsync("无可用节点", "未能从订阅中解析出任何有效节点。");
                return;
            }

            SelectedServer ??= Servers[^1];
            await SaveAsync();
        }

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


