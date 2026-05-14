using System.Collections.Generic;
using System.Linq;
using XrayUI.Models;

namespace XrayUI.Views
{
    public sealed partial class AddChainProxyDialog
    {
        private readonly List<ServerEntry> _servers;
        private readonly ServerEntry? _existing;

        public AddChainProxyDialog(IEnumerable<ServerEntry>? servers = null, ServerEntry? existing = null)
        {
            this.InitializeComponent();
            _existing = existing;
            _servers = servers?
                .Where(server => !server.IsChain)
                .ToList() ?? [];

            EntryComboBox.ItemsSource = _servers;
            ExitComboBox.ItemsSource = _servers;

            if (existing is not null)
            {
                NameTextBox.Text = existing.Name;
                EntryComboBox.SelectedItem = _servers.FirstOrDefault(
                    server => server.Id == existing.ChainEntryServerId);
                ExitComboBox.SelectedItem = _servers.FirstOrDefault(
                    server => server.Id == existing.ChainExitServerId);
            }
        }

        public bool TryCreateOrUpdate(out ServerEntry? entry)
        {
            entry = null;
            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;

            var name = NameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("请输入链式代理名称。");
                return false;
            }

            if (EntryComboBox.SelectedItem is not ServerEntry entryServer)
            {
                ShowError("请选择入口代理。");
                return false;
            }

            if (ExitComboBox.SelectedItem is not ServerEntry exitServer)
            {
                ShowError("请选择出口代理。");
                return false;
            }

            if (entryServer.Id == exitServer.Id)
            {
                ShowError("入口代理和出口代理不能是同一个节点。");
                return false;
            }

            var chain = _existing ?? new ServerEntry();
            chain.Name = name;
            chain.SubscriptionId = string.Empty;
            chain.Protocol = "chain";
            chain.Host = entryServer.Host;
            chain.Port = entryServer.Port;
            chain.Network = "chain";
            chain.Encryption = $"{entryServer.DisplayProtocol} -> {exitServer.DisplayProtocol}";
            chain.Username = string.Empty;
            chain.Password = string.Empty;
            chain.Uuid = string.Empty;
            chain.AlterId = 0;
            chain.Path = string.Empty;
            chain.WsHost = string.Empty;
            chain.Security = string.Empty;
            chain.Sni = string.Empty;
            chain.Fingerprint = string.Empty;
            chain.AllowInsecure = false;
            chain.EchConfigList = string.Empty;
            chain.EchForceQuery = string.Empty;
            chain.PublicKey = string.Empty;
            chain.ShortId = string.Empty;
            chain.SpiderX = string.Empty;
            chain.Flow = string.Empty;
            chain.VlessEncryption = string.Empty;
            chain.Finalmask = string.Empty;
            chain.ChainEntryServerId = entryServer.Id;
            chain.ChainExitServerId = exitServer.Id;
            chain.ChainEntryName = entryServer.Name;
            chain.ChainExitName = exitServer.Name;
            chain.ChainEntryProtocol = entryServer.DisplayProtocol;
            chain.ChainExitProtocol = exitServer.DisplayProtocol;

            entry = chain;
            return true;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
