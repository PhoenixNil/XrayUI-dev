using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class TunConfirmationDialog : UserControl
    {
        // Localized at construction so the resource loader is already initialized.
        private static string AutoInterfaceLabel => L.Tun_AutoInterfaceLabel;

        public TunConfirmationDialog(
            int currentMtu, string currentInterface, bool currentIpv6Enabled, bool currentLeakGuardEnabled)
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(InterfaceComboBox, L.Tun_InterfaceTooltip);
            ToolTipService.SetToolTip(LeakGuardLabelText, L.Tun_LeakGuardTooltip);
            ToolTipService.SetToolTip(LeakGuardToggleSwitch, L.Tun_LeakGuardTooltip);
            MtuNumberBox.Value = currentMtu;
            PopulateInterfaceComboBox(currentInterface);
            Ipv6ToggleSwitch.IsOn = currentIpv6Enabled;
            LeakGuardToggleSwitch.IsOn = currentLeakGuardEnabled;
        }

        public int Mtu => XrayConfigConstants.NormalizeTunMtu(
            double.IsNaN(MtuNumberBox.Value) ? XrayConfigConstants.TunMtuDefault : (int)MtuNumberBox.Value);

        public bool Ipv6Enabled => Ipv6ToggleSwitch.IsOn;

        public bool LeakGuardEnabled => LeakGuardToggleSwitch.IsOn;

        public string SelectedInterface =>
            (InterfaceComboBox.SelectedItem as ComboBoxItem)?.Tag as string
            ?? XrayConfigConstants.TunOutboundInterfaceAuto;

        private void MoreOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdvancedSettingsPanel.Visibility == Visibility.Visible)
            {
                AdvancedSettingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                AdvancedSettingsPanel.Visibility = Visibility.Visible;
            }
        }

        private void PopulateInterfaceComboBox(string selectedInterface)
        {
            InterfaceComboBox.Items.Clear();

            var autoItem = new ComboBoxItem
            {
                Content = AutoInterfaceLabel,
                Tag = XrayConfigConstants.TunOutboundInterfaceAuto,
            };
            InterfaceComboBox.Items.Add(autoItem);

            ComboBoxItem? matchingItem = null;
            foreach (var name in EnumerateActiveInterfaceNames())
            {
                var item = new ComboBoxItem { Content = name, Tag = name };
                InterfaceComboBox.Items.Add(item);
                if (string.Equals(name, selectedInterface, StringComparison.OrdinalIgnoreCase))
                    matchingItem = item;
            }

            // The persisted interface may no longer be present (Wi-Fi adapter removed,
            // VPN uninstalled, etc.) — surface it anyway so the user sees what's saved
            // and can change it deliberately.
            if (matchingItem is null
                && !string.IsNullOrWhiteSpace(selectedInterface)
                && !string.Equals(selectedInterface, XrayConfigConstants.TunOutboundInterfaceAuto, StringComparison.OrdinalIgnoreCase))
            {
                matchingItem = new ComboBoxItem { Content = selectedInterface, Tag = selectedInterface };
                InterfaceComboBox.Items.Add(matchingItem);
            }

            InterfaceComboBox.SelectedItem = matchingItem ?? autoItem;
        }

        private static List<string> EnumerateActiveInterfaceNames()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                                 && !TunService.IsTunLikeInterface(ni))
                    .Select(ni => ni.Name)
                    .OrderBy(name => name)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
    }
}
