using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public ServerListViewModel(IDialogService dialogs, SettingsService settings)
        {
            _dialogs  = dialogs;
            _settings = settings;
        }

        [ObservableProperty]
        private ObservableCollection<ServerEntry> servers = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditServer))]
        [NotifyPropertyChangedFor(nameof(CanRemoveServer))]
        private ServerEntry? selectedServer;

        public bool CanEditServer   => SelectedServer != null;
        public bool CanRemoveServer => SelectedServer != null;

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


