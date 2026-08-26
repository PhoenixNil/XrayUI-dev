using System;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using XrayUI.Controls;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Views;

namespace XrayUI.Services
{
    /// <summary>
    /// Builds and shows ContentDialogs using a deferred XamlRoot (captured on first use).
    /// </summary>
    public class DialogService : IDialogService
    {
        private readonly Func<XamlRoot?> _xamlRootFactory;

        /// <summary>
        /// Caps the wrapped message text of the plain notice dialogs. Handing ContentDialog a raw
        /// string instead lets it measure out to its 548px maximum even for a one-liner, so every
        /// short notice opens as wide as the longest one.
        /// </summary>
        private const double MessageMaxWidth = 280;

        public DialogService(Func<XamlRoot?> xamlRootFactory)
        {
            _xamlRootFactory = xamlRootFactory;
        }

        private XamlRoot XamlRoot =>
            _xamlRootFactory() ?? throw new InvalidOperationException("XamlRoot not available.");

        // ── Import link ───────────────────────────────────────────────────────

        public async Task<string?> ShowImportLinkDialogAsync()
        {
            var textBox = new TextBox
            {
                PlaceholderText = L.Import_Placeholder,
                AcceptsReturn = true,
                Width = 360,
                Height = 148,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Top
            };

            var dialog = CreateDialog();
            dialog.Title = L.Import_Title;
            dialog.PrimaryButtonText = L.Dialog_OK;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = new StackPanel
            {
                Width = 300,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = L.Import_SupportHint,
                        Opacity = 0.65,
                    },
                    textBox
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var text = textBox.Text?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        // ── Subscriptions ─────────────────────────────────────────────────────

        public async Task<SubscriptionEntry?> ShowSubscriptionsDialogAsync(ManageSubscriptionsViewModel vm)
        {
            var dialog = CreateDialog();
            dialog.Content = new ManageSubscriptionsDialog(vm);

            void SyncDialogButtons()
            {
                if (vm.IsAddPage)
                {
                    dialog.PrimaryButtonText = L.Dialog_Add;
                    dialog.CloseButtonText = L.Dialog_Cancel;
                    dialog.DefaultButton = ContentDialogButton.Primary;
                    dialog.IsPrimaryButtonEnabled = vm.CanAddSubscription;
                    return;
                }

                dialog.PrimaryButtonText = string.Empty;
                dialog.CloseButtonText = L.Dialog_Done;
                dialog.DefaultButton = ContentDialogButton.Close;
                dialog.IsPrimaryButtonEnabled = false;
            }

            PropertyChangedEventHandler handler = (_, _) => SyncDialogButtons();
            vm.PropertyChanged += handler;
            SyncDialogButtons();

            try
            {
                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary ? vm.CreateSubscription() : null;
            }
            finally
            {
                vm.PropertyChanged -= handler;
            }
        }

        // ── Edit server ───────────────────────────────────────────────────────

        public async Task<ServerEntry?> ShowEditServerDialogAsync(ServerEntry? existing)
        {
            // ── Controls ──────────────────────────────────────────────────────
            var txtName = new TextBox { Header = L.EditServer_Name, Text = existing?.Name ?? string.Empty, MinWidth = 420 };
            var txtHost = new TextBox { Header = L.EditServer_Address, Text = existing?.Host ?? string.Empty };
            var numPort = new NumberBox
            {
                Header = L.EditServer_Port, Value = existing?.Port ?? 443, Minimum = 1, Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            var cmbProtocol = new ComboBox { Header = L.EditServer_Protocol, MinWidth = 200 };
            foreach (var p in new[] { "ss", "vmess", "vless", "hysteria2", "trojan", "socks", "http", "wireguard" })
                cmbProtocol.Items.Add(new ComboBoxItem { Content = ServerEntry.GetDisplayProtocol(p), Tag = p });
            SetProtocolCode(cmbProtocol, existing?.Protocol?.ToLower() ?? "ss");

            var cmbEncryption = new ComboBox { Header = L.EditServer_Encryption, MinWidth = 200 };
            foreach (var m in new[]
                     {
                         "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "2022-blake3-aes-128-gcm",
                         "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305"
                     })
                cmbEncryption.Items.Add(m);
            if (existing?.Encryption is { Length: > 0 } existingEnc && !cmbEncryption.Items.Contains(existingEnc))
                cmbEncryption.Items.Add(existingEnc);
            cmbEncryption.SelectedItem = existing?.Encryption ?? "aes-128-gcm";
            var txtUsername = new TextBox { Header = L.EditServer_SocksUsername, Text = existing?.Username ?? string.Empty };
            var txtPassword = CreateRevealablePasswordBox(L.EditServer_Password, existing?.Password);
            var txtUuid = new TextBox { Header = "UUID (VMess / VLESS)", Text = existing?.Uuid ?? string.Empty };
            var numAlterId = new NumberBox
                { Header = "AlterId (VMess)", Value = existing?.AlterId ?? 0, Minimum = 0, Maximum = 65535 };
            var cmbNetwork = new ComboBox { Header = L.EditServer_Transport, MinWidth = 200 };
            foreach (var n in new[] { "tcp", "ws", "grpc", "xhttp" })
                cmbNetwork.Items.Add(n);
            cmbNetwork.SelectedItem = existing?.Network ?? "tcp";

            var txtPath = new TextBox { Header = L.EditServer_Path, Text = existing?.Path ?? string.Empty };
            var txtWsHost = new TextBox { Header = L.EditServer_WsHost, Text = existing?.WsHost ?? string.Empty };
            // Literal English headers — technical fields, same convention as SNI / Finalmask (JSON).
            // Blank first item = "not set": omitted from the config so xray defaults to auto.
            var cmbXhttpMode = new ComboBox { Header = "XHTTP Mode", MinWidth = 200 };
            cmbXhttpMode.Items.Add(string.Empty);
            foreach (var m in XhttpSettings.Modes)
                cmbXhttpMode.Items.Add(m);
            cmbXhttpMode.SelectedItem = XhttpSettings.NormalizeMode(existing?.XhttpMode);
            var txtXhttpExtra = CreateJsonTextBox("XHTTP Extra (JSON)", existing?.XhttpExtra);
            var cmbSecurity = new ComboBox { Header = L.EditServer_Security, MinWidth = 200 };
            foreach (var s in new[] { "none", "tls", "reality" })
                cmbSecurity.Items.Add(s);
            cmbSecurity.SelectedItem = existing?.Security ?? "none";

            var txtSni = new TextBox { Header = "SNI", Text = existing?.Sni ?? string.Empty };
            var txtFp = new TextBox { Header = L.EditServer_Fingerprint, Text = existing?.Fingerprint ?? string.Empty };
            var chkAllowInsecure = new CheckBox
                { Content = L.EditServer_AllowInsecure, IsChecked = existing?.AllowInsecure ?? false };
            // Localized on purpose, unlike the PublicKey (Reality) / Flow (VLESS) rows below:
            // it pairs with 指纹 (uTLS) above, of which 证书指纹 is the qualified form.
            var txtPinnedCert = new TextBox
            {
                Header = L.EditServer_CertFingerprint,
                Text = existing?.PinnedPeerCertSha256 ?? string.Empty,
                TextWrapping = TextWrapping.Wrap
            };
            var txtEchConfigList = new TextBox
            {
                Header = "ECH ConfigList",
                PlaceholderText = L.EditServer_EchPlaceholder,
                Text = existing?.EchConfigList ?? string.Empty,
                TextWrapping = TextWrapping.Wrap
            };
            var cmbEchForceQuery = new ComboBox { Header = "ECH Force Query", MinWidth = 200 };
            foreach (var q in new[] { EchSettings.None, EchSettings.Half, EchSettings.Full })
                cmbEchForceQuery.Items.Add(q);
            var existingEchForceQuery = EchSettings.NormalizeForceQuery(existing?.EchForceQuery);
            cmbEchForceQuery.SelectedItem = string.IsNullOrEmpty(existingEchForceQuery)
                ? EchSettings.None
                : existingEchForceQuery;
            var txtPk = new TextBox { Header = "PublicKey (Reality)", Text = existing?.PublicKey ?? string.Empty };
            var txtSid = new TextBox { Header = "ShortId (Reality)", Text = existing?.ShortId ?? string.Empty };
            var txtSpx = new TextBox { Header = "SpiderX (Reality)", Text = existing?.SpiderX ?? string.Empty };
            var txtFlow = new TextBox
            {
                Header = "Flow (VLESS)", PlaceholderText = L.EditServer_FlowPlaceholder, Text = existing?.Flow ?? string.Empty
            };
            var txtVlessEncryption = new TextBox
            {
                Header = "VLESS encryption (PQ)",
                PlaceholderText = L.EditServer_FinalmaskPlaceholder,
                Text = existing?.VlessEncryption ?? string.Empty,
                TextWrapping = TextWrapping.Wrap
            };
            var txtFinalmask = CreateJsonTextBox("Finalmask (JSON)", existing?.Finalmask);

            // WireGuard. Literal English headers, matching the other technical fields above
            // (SNI / UUID / PublicKey (Reality) / Flow (VLESS)).
            var txtWgPrivateKey = new TextBox { Header = "Private Key", Text = existing?.WgPrivateKey ?? string.Empty };
            var txtWgPublicKey = new TextBox { Header = "Peer Public Key", Text = existing?.WgPublicKey ?? string.Empty };
            var txtWgPreSharedKey = new TextBox { Header = "Pre-shared Key", Text = existing?.WgPreSharedKey ?? string.Empty };
            var txtWgLocalAddress = new TextBox
            {
                Header = "Local Address",
                PlaceholderText = "172.16.0.2/32, fd00::2/128",
                Text = existing?.WgLocalAddress ?? string.Empty
            };
            var numWgMtu = new NumberBox
            {
                Header = "MTU",
                Value = existing is { WgMtu: > 0 } ? existing.WgMtu : 1420,
                Minimum = 0,
                Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
            };
            var txtWgReserved = new TextBox { Header = "Reserved", PlaceholderText = "0,0,0", Text = existing?.WgReserved ?? string.Empty };

            // Row containers for conditional visibility
            var rowEncryption = Wrap(cmbEncryption);
            var rowUsername = Wrap(txtUsername);
            var rowPassword = Wrap(txtPassword);
            var rowUuid = Wrap(txtUuid);
            var rowAlterId = Wrap(numAlterId);
            var rowPath = Wrap(txtPath);
            var rowWsHost = Wrap(txtWsHost);
            var rowXhttpMode = Wrap(cmbXhttpMode);
            var rowXhttpExtra = Wrap(txtXhttpExtra);
            var rowSni = Wrap(txtSni);
            var rowFp = Wrap(txtFp);
            var rowAllowInsecure = Wrap(chkAllowInsecure);
            var rowPinnedCert = Wrap(txtPinnedCert);
            var rowEchConfigList = Wrap(txtEchConfigList);
            var rowEchForceQuery = Wrap(cmbEchForceQuery);
            var rowPk = Wrap(txtPk);
            var rowSid = Wrap(txtSid);
            var rowSpx = Wrap(txtSpx);
            var rowFlow = Wrap(txtFlow);
            var rowVlessEncryption = Wrap(txtVlessEncryption);
            var rowFinalmask = Wrap(txtFinalmask);
            var rowWgPrivateKey = Wrap(txtWgPrivateKey);
            var rowWgPublicKey = Wrap(txtWgPublicKey);
            var rowWgPreSharedKey = Wrap(txtWgPreSharedKey);
            var rowWgLocalAddress = Wrap(txtWgLocalAddress);
            var rowWgMtu = Wrap(numWgMtu);
            var rowWgReserved = Wrap(txtWgReserved);

            void UpdateVisibility()
            {
                var proto = GetProtocolCode(cmbProtocol) ?? "ss";
                var net = cmbNetwork.SelectedItem?.ToString() ?? "tcp";
                var sec = cmbSecurity.SelectedItem?.ToString() ?? "none";

                bool isSs = proto == "ss";
                bool isVmess = proto == "vmess";
                bool isVless = proto == "vless";
                bool isHysteria2 = proto == "hysteria2";
                bool isTrojan = proto == "trojan";
                bool isSocks = proto == "socks";
                bool isHttp = proto == "http";
                bool isWireguard = proto == "wireguard";
                bool isStandardTransport = !isHysteria2 && !isSocks && !isHttp && !isWireguard;
                bool hasWs = isStandardTransport && net == "ws";
                bool hasXhttp = isStandardTransport && net == "xhttp";
                bool hasGrpc = isStandardTransport && net == "grpc";
                bool hasTls = isStandardTransport && (sec == "tls" || sec == "reality");
                bool hasReality = isStandardTransport && sec == "reality";
                bool hasEch = isVless && sec == "tls";

                cmbNetwork.Visibility = isStandardTransport ? Visibility.Visible : Visibility.Collapsed;
                cmbSecurity.Visibility = isStandardTransport ? Visibility.Visible : Visibility.Collapsed;

                rowEncryption.Visibility = isSs ? Visibility.Visible : Visibility.Collapsed;
                rowUsername.Visibility = (isSocks || isHttp) ? Visibility.Visible : Visibility.Collapsed;
                rowPassword.Visibility = (isSs || isHysteria2 || isTrojan || isSocks || isHttp)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                rowUuid.Visibility = (isVmess || isVless) ? Visibility.Visible : Visibility.Collapsed;
                rowAlterId.Visibility = isVmess ? Visibility.Visible : Visibility.Collapsed;
                rowPath.Visibility = (hasWs || hasXhttp || hasGrpc) ? Visibility.Visible : Visibility.Collapsed;
                rowWsHost.Visibility = (hasWs || hasXhttp) ? Visibility.Visible : Visibility.Collapsed;
                rowXhttpMode.Visibility = hasXhttp ? Visibility.Visible : Visibility.Collapsed;
                rowXhttpExtra.Visibility = hasXhttp ? Visibility.Visible : Visibility.Collapsed;
                rowSni.Visibility = (hasTls || isHysteria2) ? Visibility.Visible : Visibility.Collapsed;
                rowFp.Visibility = hasTls ? Visibility.Visible : Visibility.Collapsed;
                rowAllowInsecure.Visibility = (hasTls || isHysteria2) ? Visibility.Visible : Visibility.Collapsed;
                // Every TLS outbound can pin; REALITY cannot (it authenticates by public key and
                // has no tlsSettings), so this tracks rowSni/rowAllowInsecure minus reality.
                rowPinnedCert.Visibility = ((hasTls && !hasReality) || isHysteria2)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                rowEchConfigList.Visibility = hasEch ? Visibility.Visible : Visibility.Collapsed;
                rowEchForceQuery.Visibility = hasEch ? Visibility.Visible : Visibility.Collapsed;
                rowPk.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
                rowSid.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
                rowSpx.Visibility = hasReality ? Visibility.Visible : Visibility.Collapsed;
                rowFlow.Visibility = isVless ? Visibility.Visible : Visibility.Collapsed;
                rowVlessEncryption.Visibility = isVless ? Visibility.Visible : Visibility.Collapsed;

                var wg = isWireguard ? Visibility.Visible : Visibility.Collapsed;
                rowWgPrivateKey.Visibility = wg;
                rowWgPublicKey.Visibility = wg;
                rowWgPreSharedKey.Visibility = wg;
                rowWgLocalAddress.Visibility = wg;
                rowWgMtu.Visibility = wg;
                rowWgReserved.Visibility = wg;
            }

            cmbProtocol.SelectionChanged += (_, _) =>
            {
                var proto = GetProtocolCode(cmbProtocol);
                if ((proto == "trojan" || proto == "hysteria2")
                    && cmbSecurity.SelectedItem?.ToString() == "none")
                {
                    cmbSecurity.SelectedItem = "tls";
                }

                UpdateVisibility();
            };
            cmbNetwork.SelectionChanged += (_, _) => UpdateVisibility();
            cmbSecurity.SelectionChanged += (_, _) => UpdateVisibility();
            UpdateVisibility();

            var form = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    txtName, txtHost, numPort, cmbProtocol,
                    rowEncryption, rowUsername, rowPassword, rowUuid, rowAlterId,
                    cmbNetwork, rowPath, rowWsHost, rowXhttpMode, rowXhttpExtra,
                    cmbSecurity, rowSni, rowFp, rowAllowInsecure, rowPinnedCert, rowEchConfigList, rowEchForceQuery,
                    rowPk, rowSid, rowSpx, rowFlow, rowVlessEncryption,
                    rowWgPrivateKey, rowWgPublicKey, rowWgPreSharedKey, rowWgLocalAddress, rowWgMtu, rowWgReserved,
                    rowFinalmask
                }
            };

            var scrollViewer = new ScrollView
            {
                Content = form,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
                // Padding gives the scrollbar its own gutter so it doesn't crowd the
                // form; the matching negative margin lets that gutter overlap the
                // dialog's existing right padding, so the form content stays centered
                // (left/right whitespace symmetric) and aligned with the title.
                Padding = new Thickness(0, 0, 14, 0),
                Margin = new Thickness(0, 0, -14, 0)
            };

            var dialog = CreateDialog();
            dialog.Title = existing == null ? L.EditServer_AddTitle : L.EditServer_EditTitle;
            dialog.PrimaryButtonText = L.Dialog_Save;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = scrollViewer;

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var entry = existing ?? new ServerEntry();
            entry.Name = txtName.Text.Trim();
            entry.Host = txtHost.Text.Trim();
            entry.Port = (int)numPort.Value;
            entry.Protocol = GetProtocolCode(cmbProtocol) ?? "ss";
            entry.Encryption = cmbEncryption.SelectedItem?.ToString() ?? string.Empty;
            entry.Username = txtUsername.Text.Trim();
            entry.Password = txtPassword.Password.Trim();
            entry.Uuid = txtUuid.Text.Trim();
            entry.AlterId = (int)numAlterId.Value;
            entry.Network = cmbNetwork.SelectedItem?.ToString() ?? "tcp";
            entry.Path = txtPath.Text.Trim();
            entry.WsHost = txtWsHost.Text.Trim();
            entry.XhttpMode = cmbXhttpMode.SelectedItem?.ToString() ?? string.Empty;
            entry.XhttpExtra = FinalmaskJson.NormalizeForStorage(txtXhttpExtra.Text);
            entry.Security = cmbSecurity.SelectedItem?.ToString() ?? "none";
            entry.Sni = txtSni.Text.Trim();
            entry.Fingerprint = txtFp.Text.Trim();
            entry.AllowInsecure = chkAllowInsecure.IsChecked == true;
            // Hand entry is the only path that needs cleaning: cert viewers copy digests with
            // colons (openssl, Firefox) or spaces (Windows certmgr). Measured against the shipped
            // core — it tolerates colons and either case itself, but rejects the spaced form with
            // "incorrect pinnedPeerCertSha256 length". Links and Clash configs carry bare hex, so
            // those paths store what they were given.
            entry.PinnedPeerCertSha256 = txtPinnedCert.Text
                .Trim().Replace(":", string.Empty).Replace(" ", string.Empty);
            entry.EchConfigList = txtEchConfigList.Text.Trim();
            entry.EchForceQuery = EchSettings.NormalizeForceQuery(cmbEchForceQuery.SelectedItem?.ToString());
            entry.PublicKey = txtPk.Text.Trim();
            entry.ShortId = txtSid.Text.Trim();
            entry.SpiderX = txtSpx.Text.Trim();
            entry.Flow = txtFlow.Text.Trim();
            entry.VlessEncryption = txtVlessEncryption.Text.Trim();
            entry.Finalmask = FinalmaskJson.NormalizeForStorage(txtFinalmask.Text);
            entry.WgPrivateKey = txtWgPrivateKey.Text.Trim();
            entry.WgPublicKey = txtWgPublicKey.Text.Trim();
            entry.WgPreSharedKey = txtWgPreSharedKey.Text.Trim();
            entry.WgLocalAddress = txtWgLocalAddress.Text.Trim();
            entry.WgReserved = txtWgReserved.Text.Trim();
            entry.WgMtu = double.IsNaN(numWgMtu.Value) ? 0 : (int)numWgMtu.Value;

            if (entry.Protocol == "hysteria2")
            {
                entry.Security = "tls";
            }
            else if (entry.Protocol == "wireguard")
            {
                entry.Network = string.Empty;
                entry.Security = string.Empty;
                entry.Encryption = "WireGuard";
                entry.Username = string.Empty;
                entry.Password = string.Empty;
                entry.Uuid = string.Empty;
                entry.AlterId = 0;
                entry.Path = string.Empty;
                entry.WsHost = string.Empty;
                entry.XhttpMode = string.Empty;
                entry.XhttpExtra = string.Empty;
                entry.Sni = string.Empty;
                entry.Fingerprint = string.Empty;
                entry.AllowInsecure = false;
                entry.PinnedPeerCertSha256 = string.Empty;
                entry.EchConfigList = string.Empty;
                entry.EchForceQuery = string.Empty;
                entry.PublicKey = string.Empty;
                entry.ShortId = string.Empty;
                entry.SpiderX = string.Empty;
                entry.Flow = string.Empty;
                entry.VlessEncryption = string.Empty;
                entry.Finalmask = string.Empty;
            }
            else if (entry.Protocol == "socks" || entry.Protocol == "http")
            {
                entry.Network = "tcp";
                entry.Security = "none";
                entry.Encryption = string.Empty;
                entry.Uuid = string.Empty;
                entry.AlterId = 0;
                entry.Path = string.Empty;
                entry.WsHost = string.Empty;
                entry.XhttpMode = string.Empty;
                entry.XhttpExtra = string.Empty;
                entry.Sni = string.Empty;
                entry.Fingerprint = string.Empty;
                entry.AllowInsecure = false;
                entry.PinnedPeerCertSha256 = string.Empty;
                entry.EchConfigList = string.Empty;
                entry.EchForceQuery = string.Empty;
                entry.PublicKey = string.Empty;
                entry.ShortId = string.Empty;
                entry.SpiderX = string.Empty;
                entry.Flow = string.Empty;
                entry.VlessEncryption = string.Empty;
                entry.Finalmask = string.Empty;
            }
            else
            {
                entry.Username = string.Empty;
            }

            // Switching away from WireGuard must not leave stale tunnel keys/addresses behind.
            if (entry.Protocol != "wireguard")
            {
                entry.WgPrivateKey = string.Empty;
                entry.WgPublicKey = string.Empty;
                entry.WgPreSharedKey = string.Empty;
                entry.WgLocalAddress = string.Empty;
                entry.WgReserved = string.Empty;
                entry.WgMtu = 0;
            }

            // XHTTP-only fields must not survive a transport switch (ws/grpc/tcp reuse Path/WsHost,
            // but mode/extra are meaningless outside xhttp and would silently reappear on switch-back).
            if (!string.Equals(entry.Network, "xhttp", StringComparison.OrdinalIgnoreCase))
            {
                entry.XhttpMode = string.Empty;
                entry.XhttpExtra = string.Empty;
            }

            if (!string.Equals(entry.Protocol, "vless", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Security, "tls", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.EchConfigList))
            {
                entry.EchConfigList = string.Empty;
                entry.EchForceQuery = string.Empty;
            }

            // Same rule for the certificate pin: gated on protocol AND security, not Security
            // alone, because ss shares the same combobox but BuildSsOutbound never reads it.
            bool pinApplies = entry.Protocol == "hysteria2"
                || ((entry.Protocol == "vmess" || entry.Protocol == "vless" || entry.Protocol == "trojan")
                    && string.Equals(entry.Security, "tls", StringComparison.OrdinalIgnoreCase));
            if (!pinApplies)
            {
                entry.PinnedPeerCertSha256 = string.Empty;
            }

            if (entry.Protocol != "ss" && entry.Protocol != "socks" && entry.Protocol != "http" && entry.Protocol != "wireguard")
            {
                entry.Encryption = entry.Security == "reality" ? "Reality"
                    : entry.Security == "tls" ? "TLS"
                    : "None";
            }

            return entry;
        }

        public async Task<ServerEntry?> ShowChainProxyDialogAsync(
            IEnumerable<ServerEntry> servers,
            ServerEntry? existing = null)
        {
            var content = new AddChainProxyDialog(servers, existing);
            ServerEntry? saved = null;

            var dialog = CreateDialog();
            dialog.Title = existing is null ? L.ChainProxy_AddTitle : L.ChainProxy_EditTitle;
            dialog.PrimaryButtonText = L.Dialog_Save;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = content;

            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (content.TryCreateOrUpdate(out saved))
                    return;

                args.Cancel = true;
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? saved : null;
        }

        // ── Edit local port ───────────────────────────────────────────────────

        public async Task<(int port, bool allowLan)?> ShowEditPortDialogAsync(int currentPort, bool currentAllowLan)
        {
            var numBox = new NumberBox
            {
                Header = L.EditPort_Header,
                Value = currentPort,
                Minimum = 1024,
                Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };

            var lanToggle = new ToggleSwitch
            {
                IsOn = currentAllowLan,
                OnContent = L.Dialog_On,
                OffContent = L.Dialog_Off,
                MinWidth = 0,
                Margin = new Thickness(0),
            };
            var lanRow = CreateLabelRow(L.EditPort_AllowLan, lanToggle);

            var lanAddressText = new TextBlock
            {
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var lanCopyBtn = new CopyButton
            {
                Content = "",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Background = transparentBrush,
                BorderBrush = transparentBrush,
            };
            ToolTipService.SetToolTip(lanCopyBtn, L.EditPort_CopyAddress);

            var lanAddressRow = new Grid { ColumnSpacing = 4 };
            lanAddressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lanAddressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(lanAddressText, 0);
            Grid.SetColumn(lanCopyBtn, 1);
            lanAddressRow.Children.Add(lanAddressText);
            lanAddressRow.Children.Add(lanCopyBtn);

            var lanAddress = TunService.GetLanDisplayAddress();
            int CurrentPortValue() => double.IsNaN(numBox.Value) ? currentPort : (int)numBox.Value;

            void UpdateLanAddressText()
            {
                if (!lanToggle.IsOn)
                {
                    lanAddressRow.Visibility = Visibility.Collapsed;
                    return;
                }

                if (lanAddress is not null)
                {
                    var address = $"{lanAddress}:{CurrentPortValue()}";
                    lanAddressText.Text = Loc.Format("EditPort_LanAddress", address);
                    lanCopyBtn.TextToCopy = address;
                    lanCopyBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    lanAddressText.Text = L.EditPort_LanUnavailable;
                    lanCopyBtn.Visibility = Visibility.Collapsed;
                }
                lanAddressRow.Visibility = Visibility.Visible;
            }

            lanToggle.Toggled += (_, _) => UpdateLanAddressText();
            numBox.ValueChanged += (_, _) => UpdateLanAddressText();
            UpdateLanAddressText();

            var dialog = CreateDialog();
            dialog.Title = L.EditPort_Title;
            dialog.PrimaryButtonText = L.Dialog_OK;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = new StackPanel
            {
                Width = 260,
                Spacing = 12,
                Children =
                {
                    numBox,
                    new TextBlock
                    {
                        Text = Loc.Format("EditPort_Range", numBox.Minimum, numBox.Maximum),
                        Opacity = 0.65,
                    },
                    lanRow,
                    lanAddressRow,
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            return (CurrentPortValue(), lanToggle.IsOn);
        }

        // ── Error ─────────────────────────────────────────────────────────────

        public async Task<bool> ShowConfirmationAsync(string title, string message, string? confirmText = null,
            string? cancelText = null, bool isDanger = false)
        {
            confirmText ??= L.Dialog_OK;
            cancelText  ??= L.Dialog_Cancel;
            var content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MessageMaxWidth,
            };

            var dialog = CreateDialog();
            dialog.Title = title;
            dialog.Content = content;
            dialog.PrimaryButtonText = confirmText;
            dialog.CloseButtonText = cancelText;
            dialog.DefaultButton = isDanger ? ContentDialogButton.None : ContentDialogButton.Primary;

            if (isDanger && Application.Current.Resources.TryGetValue("DangerAccentButtonStyle", out var style) &&
                style is Style buttonStyle)
                dialog.PrimaryButtonStyle = buttonStyle;

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        public async Task<bool> ShowTunConfirmationDialogAsync(AppSettings settings)
        {
            var content = new TunConfirmationDialog(settings.TunMtu, settings.TunOutboundInterface, settings.TunIpv6Enabled);

            var dialog = CreateDialog();
            dialog.Title = L.Tun_EnableTitle;
            dialog.Content = content;
            dialog.PrimaryButtonText = L.Dialog_Confirm;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            settings.TunMtu = content.Mtu;
            settings.TunOutboundInterface = content.SelectedInterface;
            settings.TunIpv6Enabled = content.Ipv6Enabled;
            return true;
        }

        public async Task<(bool cleared, uint mods, uint vk)?> ShowHotkeyRecorderDialogAsync(string title, uint currentMods, uint currentVk)
        {
            var content = new HotkeyRecorderControl(currentMods, currentVk);

            var dialog = CreateDialog();
            dialog.Title = title;
            dialog.Content = content;
            dialog.PrimaryButtonText = L.Dialog_Save;
            dialog.SecondaryButtonText = L.Personalize_HotkeyDialogClear;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.IsPrimaryButtonEnabled = currentVk != 0;
            dialog.IsSecondaryButtonEnabled = currentVk != 0;
            dialog.DefaultButton = ContentDialogButton.Primary;

            content.ComboCaptured += (_, _) => dialog.IsPrimaryButtonEnabled = true;

            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary   => (false, content.Mods, content.Vk),
                ContentDialogResult.Secondary => (true, 0u, 0u),
                _                              => null,
            };
        }

        public async Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null)
        {
            var dialog = CreateDialog(xamlRoot);
            dialog.Title = title;
            dialog.Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MessageMaxWidth,
            };
            dialog.CloseButtonText = L.Dialog_OK;
            await dialog.ShowAsync();
        }

        // ── Progress ──────────────────────────────────────────────────────────

        public async Task ShowProgressBarDialogAsync(string title,
            Func<IProgress<ProgressDialogUpdate>, CancellationToken, Task> work, XamlRoot? xamlRoot = null)
        {
            using var cts = new CancellationTokenSource();

            var statusText = new TextBlock
            {
                Text = L.Dialog_Preparing,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Minimum = 0,
                Maximum = 100,
                Width = 320,
            };

            var dialog = CreateDialog(xamlRoot);
            dialog.Title = title;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { progressBar, statusText }
            };

            var progress = new Progress<ProgressDialogUpdate>(update =>
            {
                statusText.Text = update.Message;

                if (update.Percent.HasValue)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = Math.Clamp(update.Percent.Value, 0, 100);
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                }
            });

            Exception? error = null;
            int workFinished = 0;

            dialog.Opened += (_, _) =>
            {
                if (Volatile.Read(ref workFinished) == 1)
                {
                    try
                    {
                        dialog.Hide();
                    }
                    catch
                    {
                    }
                }
            };

            var workTask = Task.Run(async () =>
            {
                try
                {
                    await work(progress, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // Real user cancel — swallow here, we rethrow a fresh OCE below based on cts state.
                    // Any *other* OperationCanceledException (e.g. HttpClient.Timeout throwing
                    // TaskCanceledException with its own internal token) must not be swallowed —
                    // it falls through to the generic catch so the caller can surface the failure.
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    Volatile.Write(ref workFinished, 1);
                    dialog.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            dialog.Hide();
                        }
                        catch
                        {
                        }
                    });
                }
            });

            await dialog.ShowAsync();

            // If the dialog closed because the user clicked Cancel (work still running), signal it.
            if (Volatile.Read(ref workFinished) == 0) cts.Cancel();

            await workTask;

            if (error != null) throw error;
            if (cts.IsCancellationRequested) throw new OperationCanceledException(cts.Token);
        }

        // ── Share link ────────────────────────────────────────────────────────

        public async Task ShowShareLinkDialogAsync(string serverName, string link)
        {
            var dialog = CreateDialog();

            // ── X close button ────────────────────────────────────────────────
            var closeBtn = new Button
            {
                Content = "\uE711",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle))
                closeBtn.Style = (Style)subtleStyle;
            closeBtn.Click += (_, _) => dialog.Hide();

            // ── Header row (title + X), placed in Content for guaranteed stretch
            var header = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = L.Share_Title,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(titleText);
            header.Children.Add(closeBtn);

            // ── Link box ──────────────────────────────────────────────────────
            var linkBox = new TextBox
            {
                Text = link,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
            };


            // ── Name row (server name + animated copy icon button) ────────────
            var nameCopyBtn = new Button
            {
                Content = "\uE8C8",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle2))
                nameCopyBtn.Style = (Style)subtleStyle2;
            ToolTipService.SetToolTip(nameCopyBtn, L.Share_CopyLink);

            nameCopyBtn.Click += async (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(link);
                Clipboard.SetContent(pkg);
                nameCopyBtn.Content = "\uE73E";
                await Task.Delay(1500);
                nameCopyBtn.Content = "\uE8C8";
            };

            var nameRow = new Grid { ColumnSpacing = 4 };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameText = new TextBlock
            {
                Text = serverName,
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(nameCopyBtn, 1);
            nameRow.Children.Add(nameText);
            nameRow.Children.Add(nameCopyBtn);

            // ── Assemble: no dialog.Title → title area collapses
            //              no CloseButtonText → bottom bar hidden
            dialog.Content = new StackPanel
            {
                Width = 360,
                Spacing = 12,
                Children =
                {
                    header,
                    nameRow,
                    linkBox,
                }
            };

            await dialog.ShowAsync();
        }

        // ── Startup ───────────────────────────────────────────────────────────

        public async Task<(bool enabled, bool autoConnect)?> ShowStartupDialogAsync(bool currentEnabled,
            bool currentAutoConnect)
        {
            var toggle = new ToggleSwitch
            {
                IsOn = currentEnabled,
                OnContent = L.Dialog_On,
                OffContent = L.Dialog_Off,
                MinWidth = 0,
                Margin = new Thickness(0),
            };

            var toggleLabel = new TextBlock
            {
                Text = L.Startup_AutoStart,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var toggleRow = new Grid { ColumnSpacing = 8 };
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(toggleLabel, 0);
            Grid.SetColumn(toggle, 1);
            toggleRow.Children.Add(toggleLabel);
            toggleRow.Children.Add(toggle);

            var checkBox = new CheckBox
            {
                Content = L.Startup_AutoConnect,
                IsChecked = currentAutoConnect,
                IsEnabled = currentEnabled,
                Margin = new Thickness(16, 0, 0, 0),
            };

            toggle.Toggled += (_, _) => checkBox.IsEnabled = toggle.IsOn;

            var dialog = CreateDialog();
            dialog.Title = L.Startup_Title;
            dialog.PrimaryButtonText = L.Dialog_Confirm;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = new StackPanel
            {
                Width = 260,
                Spacing = 12,
                Children = { toggleRow, checkBox },
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            return (toggle.IsOn, checkBox.IsChecked == true);
        }

        // ── App update confirm ────────────────────────────────────────────────

        /// <summary>
        /// Content width of the update dialog, inside ContentDialog's own padding. Sized so a
        /// typical changelog line still fits on one row; longer ones wrap onto the hanging
        /// indent that <see cref="BuildNoteLine"/> sets up, which is why this can be tightened
        /// well below what the longest line would need.
        /// </summary>
        private const double UpdateContentWidth = 352;

        /// <summary>Right gutter kept clear inside the notes list for the overlay scrollbar.</summary>
        private const double ScrollBarGutter = 14;

        /// <summary>
        /// How far down the dialog the gradient reaches, in DIPs. Pinned in absolute units
        /// rather than a fraction of the surface so a three-note release and a ten-note one
        /// get the same header wash instead of the gradient stretching with the note count.
        /// Deliberately deeper than the coloured area looks: low amplitude over a large area
        /// reads as atmosphere, while the same colour packed into a shallower band reads as a
        /// stripe across the top.
        /// </summary>
        private const double UpdateGradientDepth = 260;

        public async Task<bool> ShowUpdateConfirmDialogAsync(
            Version newVersion, IReadOnlyList<string> notes)
        {
            var dialog = CreateDialog();
            // Neither dialog.Title nor the Primary/Close button text is set, and both omissions
            // are load-bearing. The template puts Title inside the padded content Grid, so a
            // real Title would sit above the gradient instead of on it; and leaving all three
            // button texts empty drives the template's NoneVisible state, which collapses
            // CommandSpace entirely. That is the only way to escape the command area's two
            // equal star columns, which stretch each button across half the dialog — the
            // buttons below are ordinary right-aligned ones laid out in the content instead.
            var pad = ContentDialogPadding();

            // Grid root, not StackPanel: a StackPanel root breaks the measure chain and
            // ContentDialog clips tall content instead of letting the notes list scroll.
            // Fixed width keeps the dialog compact — without it the longest note line
            // stretches it toward ContentDialog's max width.
            var root = new Grid { Width = UpdateContentWidth };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // heading
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // notes header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // notes list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

            // Added first so everything else draws over it, and spanning every row so its own
            // height decides how far the colour reaches instead of the heading row clipping it.
            // The negative margin cancels the dialog's padding, so it bleeds to the left, right
            // and top edges — the rounded DialogSpace clips its corners.
            var wash = BuildGradientWash();
            Grid.SetRowSpan(wash, root.RowDefinitions.Count);
            wash.Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, 0);
            root.Children.Add(wash);

            var heading = new TextBlock
            {
                // 20 is what ContentDialog's own Title uses; going larger made the dialog read
                // top-heavy and, with it, bigger than it is.
                Text = Loc.Format("Update_ConfirmTitle", newVersion),
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 18),
            };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            if (notes.Count > 0)
            {
                // Opacity instead of TextFillColorSecondaryBrush: Application.Current.Resources
                // resolves theme brushes against the app-level theme (never set here), which goes
                // stale under the Personalize theme override — see Views/LogWindow.xaml.
                var notesHeader = new TextBlock
                {
                    Text = L.Update_ConfirmNotesHeader,
                    FontSize = 12,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                Grid.SetRow(notesHeader, 1);
                root.Children.Add(notesHeader);

                var list = new StackPanel { Spacing = 6 };
                foreach (var line in notes)
                    list.Children.Add(BuildNoteLine(line));

                var scroller = new ScrollViewer
                {
                    Content = list,
                    MaxHeight = 240,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    // WinUI scrollbars are overlays: they paint on top of the content rather
                    // than taking layout space, so without this gutter the expanded mouse
                    // scrollbar covers the last dozen pixels of every note line. Whether it
                    // overlays at all depends on the "always show scrollbars" accessibility
                    // setting, so reserving the space is the only way to get the same result
                    // under both. The gutter is unconditional because whether the list will
                    // overflow is not known until after layout.
                    Padding = new Thickness(0, 0, ScrollBarGutter, 0),
                };
                Grid.SetRow(scroller, 2);
                root.Children.Add(scroller);
            }

            var confirmed = false;

            var updateNow = new Button
            {
                Content = L.Update_ConfirmNow,
                MinWidth = 104,
                Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            };
            updateNow.Click += (_, _) => { confirmed = true; dialog.Hide(); };

            var later = new Button { Content = L.Update_ConfirmLater, MinWidth = 88 };
            later.Click += (_, _) => dialog.Hide();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 20, 0, 0),
                Children = { updateNow, later },
            };
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            // ContentDialog.DefaultButton only drives the template's own buttons, so Enter has
            // nothing to activate here without this. Escape still dismisses the dialog on its
            // own and leaves confirmed false, which is the right answer for a cancel.
            // Pointer rather than Programmatic focus: both make Enter work, but Programmatic
            // draws the keyboard focus ring, which reads as a heavy double border around the
            // accent button. Tabbing brings the ring back, as it should.
            dialog.Opened += (_, _) => updateNow.Focus(FocusState.Pointer);

            dialog.Content = root;
            await dialog.ShowAsync();
            return confirmed;
        }

        /// <summary>
        /// The soft mesh gradient behind the whole dialog. Three stacked
        /// <see cref="RadialGradientBrush"/> layers are what make it read as a mesh — one brush
        /// can only paint one blob, and Background takes exactly one. Every layer is centred on
        /// the top edge and falls off to zero alpha within <see cref="UpdateGradientDepth"/>,
        /// so the plain dialog background takes over well above the buttons with no seam.
        /// </summary>
        private static Grid BuildGradientWash()
        {
            var wash = new Grid { VerticalAlignment = VerticalAlignment.Top, Height = UpdateGradientDepth };
            var width = UpdateContentWidth + ContentDialogPadding().Left * 2;

            // Amber and rose are neighbours on the wheel, so together they read as one colour
            // family rather than as competing hues; the lilac is held down to a whisper as the
            // single accent. An earlier pass used violet, blue and mint at equal weight and it
            // looked like a rainbow stripe — hue count, not gradient quality, was the problem.
            // Warm was picked over cool on purpose: the primary button follows the user's
            // Windows accent colour, which is blue for most people, and a warm field is what
            // keeps that button reading as the one thing to click.
            // Centres are staggered rather than all sitting on the top edge, which is what
            // leaves a pale valley in the middle instead of one continuous band of colour.
            //
            // Dark is not the light set at lower alpha: a saturated warm composited over a dark
            // grey surface goes to brown mud (verified by rendering it). The dark hues are the
            // same colours already lifted toward pale, which keeps them reading as warm light.
            var dark = ThemeHelper.ActualTheme == ElementTheme.Dark;
            var (amber, amberAlpha) = dark ? (0xF7D9A6u, 0.26) : (0xF0B860u, 0.34);
            var (rose, roseAlpha)   = dark ? (0xF5C0B8u, 0.24) : (0xEE9B92u, 0.32);
            var (lilac, lilacAlpha) = dark ? (0xD8CBEEu, 0.08) : (0xC0A8E0u, 0.10);

            wash.Children.Add(GradientBlob(amber, amberAlpha,
                centerX: 0.08 * width, centerY: -40, radiusX: 300, radiusY: 260));
            wash.Children.Add(GradientBlob(rose, roseAlpha,
                centerX: 1.02 * width, centerY: 0, radiusX: 290, radiusY: 250));
            wash.Children.Add(GradientBlob(lilac, lilacAlpha,
                centerX: 1.00 * width, centerY: 170, radiusX: 240, radiusY: 210));
            return wash;
        }

        /// <summary>
        /// One colour blob of the mesh: an empty Border stretched over the wash, painted with a
        /// radial gradient that falls off to the same colour at zero alpha (fading to a named
        /// transparent would drift through grey on the way out). Absolute mapping mode, so the
        /// blob keeps its shape no matter how tall the dialog grows.
        /// </summary>
        private static Border GradientBlob(
            uint rgb, double alpha,
            double centerX, double centerY, double radiusX, double radiusY)
        {
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            return new Border
            {
                Background = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = new Point(centerX, centerY),
                    GradientOrigin = new Point(centerX, centerY),
                    RadiusX = radiusX,
                    RadiusY = radiusY,
                    GradientStops =
                    {
                        // Slightly faster than linear, so the colour stays gathered around its
                        // centre and releases cleanly instead of holding a flat plateau.
                        new GradientStop { Color = Color.FromArgb((byte)(alpha * 255), r, g, b), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb((byte)(alpha * 0.42 * 255), r, g, b), Offset = 0.5 },
                        new GradientStop { Color = Color.FromArgb(0, r, g, b), Offset = 1 },
                    },
                },
            };
        }

        /// <summary>
        /// ContentDialog's content padding, read from the theme resource so the banner's bleed
        /// margin follows if WinUI retunes it. A Thickness is theme-invariant, so this app-level
        /// lookup is safe in a way the brush lookups called out above are not.
        /// </summary>
        private static Thickness ContentDialogPadding() =>
            Application.Current.Resources.TryGetValue("ContentDialogPadding", out var value)
            && value is Thickness padding
                ? padding
                : new Thickness(24);

        /// <summary>
        /// One bullet as a two-column Grid rather than a "• "-prefixed string, so wrapped
        /// lines keep a hanging indent instead of running back under the bullet.
        /// </summary>
        private static Grid BuildNoteLine(string text)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = "•",
                FontSize = 14,
                LineHeight = 20,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var body = new TextBlock
            {
                Text = text,
                FontSize = 14,
                // Default leading is tight for CJK once a line wraps; 20 keeps a wrapped note
                // readable without spacing the list out.
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(body, 1);

            row.Children.Add(bullet);
            row.Children.Add(body);
            return row;
        }

        // ── DNS settings ──────────────────────────────────────────────────────

        public async Task<bool> ShowDnsSettingsDialogAsync(AppSettings settings, bool isTunMode)
        {
            var directBox = new TextBox
            {
                Text = settings.DirectDnsServer ?? string.Empty,
                PlaceholderText = L.Dns_ServerPlaceholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var proxyBox = new TextBox
            {
                Text = settings.ProxyDnsServer ?? string.Empty,
                PlaceholderText = L.Dns_ServerPlaceholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var directPresets = CreatePresetButtons(directBox,
                (L.Dns_Provider_Ali, "223.5.5.5"),
                (L.Dns_Provider_Tencent, "119.29.29.29"),
                ("114",  "114.114.114.114"),
                ("DoH",  "https://dns.alidns.com/dns-query"));

            var proxyPresets = CreatePresetButtons(proxyBox,
                (L.Dns_Provider_Google, "8.8.8.8"),
                ("CF", "1.1.1.1"),
                ("Quad9", "9.9.9.9"),
                ("DoH", "https://cloudflare-dns.com/dns-query"));

            var strategyCmb = new ComboBox { MinWidth = 100 };
            foreach (var item in new[] { L.Dns_Strategy_V4Only, L.Dns_Strategy_V6Only, L.Dns_Strategy_Auto })
                strategyCmb.Items.Add(item);
            strategyCmb.SelectedIndex = settings.DnsQueryStrategy switch
            {
                DnsQueryStrategy.IPv6 => 1,
                DnsQueryStrategy.Any => 2,
                _ => 0,
            };

            var cacheSwitch = new ToggleSwitch
            {
                IsOn = settings.DnsCacheEnabled,
                OnContent = L.Dialog_On,
                OffContent = L.Dialog_Off,
                MinWidth = 0,
                Margin = new Thickness(0),
            };

            var fakeDnsSwitch = new ToggleSwitch
            {
                IsOn = settings.FakeDnsEnabled && isTunMode,
                IsEnabled = isTunMode,
                OnContent = L.Dialog_On,
                OffContent = L.Dialog_Off,
                MinWidth = 0,
                Margin = new Thickness(0),
            };

            var fakeDnsTitleText = new TextBlock
            {
                Text = "FakeDNS",
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(fakeDnsTitleText, L.Dns_TunOnlyHint);

            var fakeDnsLabel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    fakeDnsTitleText,
                    new TextBlock
                    {
                        Text = L.Dns_Experimental,
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground =
                            (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                                "SystemFillColorAttentionBrush"],
                    },
                },
            };

            var fakeDnsRow = new Grid { ColumnSpacing = 8 };
            fakeDnsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fakeDnsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(fakeDnsLabel, 0);
            Grid.SetColumn(fakeDnsSwitch, 1);
            fakeDnsRow.Children.Add(fakeDnsLabel);
            fakeDnsRow.Children.Add(fakeDnsSwitch);

            var strategyRow = CreateLabelRow(L.Dns_QueryStrategyLabel, strategyCmb);
            var cacheRow = CreateLabelRow(L.Dns_EnableCacheLabel, cacheSwitch);

            var content = new StackPanel
            {
                Width = 340,
                Spacing = 20,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = L.Dns_DirectTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = L.Dns_DirectDesc,
                                FontSize = 11,
                                Opacity = 0.6,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 4),
                            },
                            directBox,
                            directPresets,
                        }
                    },
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = L.Dns_ProxyTitle, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = L.Dns_ProxyDesc,
                                FontSize = 11,
                                Opacity = 0.6,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 4),
                            },
                            proxyBox,
                            proxyPresets,
                        }
                    },
                    new StackPanel { Spacing = 14, Children = { strategyRow, cacheRow, fakeDnsRow } },
                }
            };

            var dialog = CreateDialog();
            dialog.Title = L.Dns_DialogTitle;
            dialog.PrimaryButtonText = L.Dialog_Save;
            dialog.SecondaryButtonText = L.Dns_ResetDefaults;
            dialog.CloseButtonText = L.Dialog_Cancel;
            dialog.DefaultButton = ContentDialogButton.Primary;
            dialog.Content = content;

            dialog.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                directBox.Text = string.Empty;
                proxyBox.Text = string.Empty;
                strategyCmb.SelectedIndex = 0;
                cacheSwitch.IsOn = true;
                fakeDnsSwitch.IsOn = false;
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return false;

            settings.DirectDnsServer = string.IsNullOrWhiteSpace(directBox.Text) ? null : directBox.Text.Trim();
            settings.ProxyDnsServer = string.IsNullOrWhiteSpace(proxyBox.Text) ? null : proxyBox.Text.Trim();
            settings.DnsQueryStrategy = strategyCmb.SelectedIndex switch
            {
                1 => DnsQueryStrategy.IPv6,
                2 => DnsQueryStrategy.Any,
                _ => DnsQueryStrategy.IPv4,
            };
            settings.DnsCacheEnabled = cacheSwitch.IsOn;
            // FakeDNS only meaningful in TUN mode. Don't clobber a previously-saved value
            // when the dialog opened in non-TUN mode (toggle was forced OFF for display).
            if (isTunMode)
            {
                settings.FakeDnsEnabled = fakeDnsSwitch.IsOn;
            }

            return true;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a ContentDialog pre-wired with the correct XamlRoot and theme.
        /// Use object-initializer syntax to set the remaining properties.
        /// </summary>
        /// <param name="xamlRootOverride">If supplied, roots the dialog in this window instead of the MainWindow factory.</param>
        private ContentDialog CreateDialog(XamlRoot? xamlRootOverride = null) => new ContentDialog
        {
            XamlRoot = xamlRootOverride ?? XamlRoot,
            RequestedTheme = ThemeHelper.ActualTheme,
        };

        private static Border Wrap(FrameworkElement child) =>
            new Border { Child = child };


        private const string RevealGlyph = "\uE7B3";   // RedEye
        private const string ConcealGlyph = "\uED1A";  // Hide

        /// <summary>
        /// A PasswordBox whose header carries a reveal toggle on the right.
        /// <para>
        /// PasswordRevealMode is forced to Hidden rather than left at the default Peek: the
        /// built-in peek button only shows up once the box has focus <em>and the user has typed
        /// a character</em>. An existing server's password is assigned here in code, so peek is
        /// dead on arrival and the value can never be read back (issue #124).
        /// </para>
        /// </summary>
        private static PasswordBox CreateRevealablePasswordBox(string header, string? value)
        {
            var box = new PasswordBox { Password = value ?? string.Empty };

            var icon = new FontIcon { FontSize = 14 };
            var toggle = new Button
            {
                Content = icon,
                Padding = new Thickness(6, 0, 6, 0),
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle))
                toggle.Style = (Style)subtleStyle;

            void SetRevealed(bool revealed)
            {
                box.PasswordRevealMode = revealed ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
                icon.Glyph = revealed ? ConcealGlyph : RevealGlyph;

                var label = revealed ? L.EditServer_HidePassword : L.EditServer_ShowPassword;
                ToolTipService.SetToolTip(toggle, label);
                AutomationProperties.SetName(toggle, label);
            }

            SetRevealed(false);
            toggle.Click += (_, _) => SetRevealed(box.PasswordRevealMode != PasswordRevealMode.Visible);

            box.Header = CreateLabelRow(header, toggle);
            return box;
        }

        /// <summary>Multi-line raw-JSON editor box (Finalmask / XHTTP extra).</summary>
        private static TextBox CreateJsonTextBox(string header, string? value) => new()
        {
            Header = header,
            // AcceptsReturn must be set BEFORE Text — initializer assigns properties in
            // declared order, and Text setter in single-line mode truncates at the first \r.
            AcceptsReturn = true,
            Height = 104,
            TextWrapping = TextWrapping.NoWrap,
            Text = (value ?? string.Empty).Replace("\r\n", "\r").Replace("\n", "\r"),
        };

        /// <summary>
        /// The protocol ComboBox shows display names (e.g. "Shadowsocks") but stores the
        /// stable business code (e.g. "ss") on each item's Tag, per the code-vs-display-string
        /// convention used for persisted/compared state elsewhere in the app.
        /// </summary>
        private static string? GetProtocolCode(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private static void SetProtocolCode(ComboBox combo, string code)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem { Tag: string tag } cbi && string.Equals(tag, code, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
        }

        /// <summary>
        /// Two-column row: stretchable label on the left, fixed-size control on the right.
        /// </summary>
        private static Grid CreateLabelRow(string label, FrameworkElement control)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            return grid;
        }

        /// <summary>
        /// Horizontal row of pill-shaped preset buttons that write their value into <paramref name="target"/>.
        /// </summary>
        private static StackPanel CreatePresetButtons(TextBox target, params (string label, string value)[] presets)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 2, 0, 0),
            };
            foreach (var (label, value) in presets)
            {
                var captured = value;
                var btn = new Button
                {
                    Content = label,
                    Padding = new Thickness(10, 3, 10, 3),
                    FontSize = 11,
                    CornerRadius = new CornerRadius(12),
                };
                btn.Click += (_, _) => target.Text = captured;
                panel.Children.Add(btn);
            }

            return panel;
        }
    }
}
