using System.Collections.Generic;
using System.Linq;
using XrayUI.Helpers;
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
                ShowError(L.ChainProxy_NameRequired);
                return false;
            }

            if (EntryComboBox.SelectedItem is not ServerEntry entryServer)
            {
                ShowError(L.ChainProxy_EntryRequired);
                return false;
            }

            if (ExitComboBox.SelectedItem is not ServerEntry exitServer)
            {
                ShowError(L.ChainProxy_ExitRequired);
                return false;
            }

            if (entryServer.Id == exitServer.Id)
            {
                ShowError(L.ChainProxy_EntryExitSame);
                return false;
            }

            var chain = _existing ?? new ServerEntry();
            chain.Name = name;
            chain.SubscriptionId = string.Empty;
            chain.Protocol = "chain";
            // No address of its own: a chain dials its entry hop, resolved by id wherever it is
            // used. Cleared so an edited chain drops the copy older versions stored here.
            chain.Host = string.Empty;
            chain.Port = 0;
            chain.ChainEntryServerId = entryServer.Id;
            chain.ChainExitServerId = exitServer.Id;

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
