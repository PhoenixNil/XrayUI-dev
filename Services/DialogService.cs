using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// Builds and shows ContentDialogs using a deferred XamlRoot (captured on first use).
    /// </summary>
    public class DialogService : IDialogService
    {
        private readonly Func<XamlRoot?> _xamlRootFactory;

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
                PlaceholderText = "粘贴节点链接（支持多协议）",
                AcceptsReturn   = true,
                Width           = 360,
                Height          = 148,
                TextWrapping    = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                VerticalContentAlignment = VerticalAlignment.Top
            };

            var dialog = CreateDialog();
            dialog.Title             = "导入节点链接";
            dialog.PrimaryButtonText = "确定";
            dialog.CloseButtonText   = "取消";
            dialog.DefaultButton     = ContentDialogButton.Primary;
            dialog.Content = new StackPanel
            {
                Width    = 300,
                Spacing  = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text    = "支持常见协议链接",
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

        // ── Add subscription ──────────────────────────────────────────────────

        public async Task<SubscriptionEntry?> ShowAddSubscriptionDialogAsync()
        {
            var txtUrl = new TextBox
            {
                Header          = "订阅链接",
                PlaceholderText = "https://...",
                Width           = 320,
                TextWrapping    = TextWrapping.Wrap,
            };

            var txtName = new TextBox
            {
                Header          = "备注名称（可选）",
                PlaceholderText = "留空则使用链接域名",
                Width           = 320,
            };

            var hint = new TextBlock
            {
                Text    = "将自动拉取并导入订阅中的全部节点",
                FontSize = 12,
                Opacity  = 0.65,
            };

            var content = new StackPanel
            {
                Width    = 320,
                Spacing  = 14,
                Margin   = new Thickness(0, 4, 0, 0),
                Children = { txtUrl, txtName, hint }
            };

            var dialog = CreateDialog();
            dialog.Title                  = "添加订阅";
            dialog.PrimaryButtonText      = "添加";
            dialog.CloseButtonText        = "取消";
            dialog.DefaultButton          = ContentDialogButton.Primary;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.Content                = content;

            txtUrl.TextChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(txtUrl.Text);

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url)) return null;

            var name = string.IsNullOrWhiteSpace(txtName.Text)
                ? TryGetHost(url)
                : txtName.Text.Trim();

            return new SubscriptionEntry { Url = url, Name = name };
        }

        private static string TryGetHost(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        // ── Edit server ───────────────────────────────────────────────────────

        public async Task<ServerEntry?> ShowEditServerDialogAsync(ServerEntry? existing)
        {
            // ── Controls ──────────────────────────────────────────────────────
            var txtName     = new TextBox { Header = "名称", Text = existing?.Name ?? string.Empty, MinWidth = 420 };
            var txtHost     = new TextBox { Header = "地址 / 域名", Text = existing?.Host ?? string.Empty };
            var numPort     = new NumberBox { Header = "端口", Value = existing?.Port ?? 443, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            var cmbProtocol = new ComboBox  { Header = "协议", MinWidth = 200 };
            foreach (var p in new[] { "ss", "vmess", "vless", "hysteria2" })
                cmbProtocol.Items.Add(p);
            cmbProtocol.SelectedItem = existing?.Protocol?.ToLower() ?? "ss";

            var txtEncryption = new TextBox { Header = "加密方式 (SS)", Text = existing?.Encryption ?? string.Empty };
            var txtPassword   = new PasswordBox { Header = "密码", Password = existing?.Password ?? string.Empty };
            var txtUuid       = new TextBox { Header = "UUID (VMess / VLESS)", Text = existing?.Uuid ?? string.Empty };
            var numAlterId    = new NumberBox { Header = "AlterId (VMess)", Value = existing?.AlterId ?? 0, Minimum = 0, Maximum = 65535 };
            var cmbNetwork    = new ComboBox { Header = "传输协议", MinWidth = 200 };
            foreach (var n in new[] { "tcp", "ws", "grpc" })
                cmbNetwork.Items.Add(n);
            cmbNetwork.SelectedItem = existing?.Network ?? "tcp";

            var txtPath     = new TextBox { Header = "路径 (WS/gRPC)", Text = existing?.Path ?? string.Empty };
            var txtWsHost   = new TextBox { Header = "WS Host 头", Text = existing?.WsHost ?? string.Empty };
            var cmbSecurity = new ComboBox { Header = "安全", MinWidth = 200 };
            foreach (var s in new[] { "none", "tls", "reality" })
                cmbSecurity.Items.Add(s);
            cmbSecurity.SelectedItem = existing?.Security ?? "none";

            var txtSni  = new TextBox { Header = "SNI", Text = existing?.Sni ?? string.Empty };
            var txtFp   = new TextBox { Header = "指纹 (uTLS)", Text = existing?.Fingerprint ?? string.Empty };
            var chkAllowInsecure = new CheckBox { Content = "允许不安全连接（跳过证书校验）", IsChecked = existing?.AllowInsecure ?? false };
            var txtPk   = new TextBox { Header = "PublicKey (Reality)", Text = existing?.PublicKey ?? string.Empty };
            var txtSid  = new TextBox { Header = "ShortId (Reality)", Text = existing?.ShortId ?? string.Empty };
            var txtSpx  = new TextBox { Header = "SpiderX (Reality)", Text = existing?.SpiderX ?? string.Empty };
            var txtFlow = new TextBox { Header = "Flow (VLESS)", PlaceholderText = "xtls-rprx-vision 或留空", Text = existing?.Flow ?? string.Empty };

            // Row containers for conditional visibility
            var rowEncryption = Wrap(txtEncryption);
            var rowPassword   = Wrap(txtPassword);
            var rowUuid       = Wrap(txtUuid);
            var rowAlterId    = Wrap(numAlterId);
            var rowPath       = Wrap(txtPath);
            var rowWsHost     = Wrap(txtWsHost);
            var rowSni        = Wrap(txtSni);
            var rowFp         = Wrap(txtFp);
            var rowAllowInsecure = Wrap(chkAllowInsecure);
            var rowPk         = Wrap(txtPk);
            var rowSid        = Wrap(txtSid);
            var rowSpx        = Wrap(txtSpx);
            var rowFlow       = Wrap(txtFlow);

            void UpdateVisibility()
            {
                var proto = cmbProtocol.SelectedItem?.ToString() ?? "ss";
                var net   = cmbNetwork.SelectedItem?.ToString() ?? "tcp";
                var sec   = cmbSecurity.SelectedItem?.ToString() ?? "none";

                bool isSs        = proto == "ss";
                bool isVmess     = proto == "vmess";
                bool isVless     = proto == "vless";
                bool isHysteria2 = proto == "hysteria2";
                bool hasWs       = net == "ws";
                bool hasTls      = sec == "tls" || sec == "reality";
                bool hasReality  = sec == "reality";

                rowEncryption.Visibility = isSs                     ? Visibility.Visible : Visibility.Collapsed;
                rowPassword  .Visibility = (isSs || isHysteria2)    ? Visibility.Visible : Visibility.Collapsed;
                rowUuid      .Visibility = (isVmess || isVless)      ? Visibility.Visible : Visibility.Collapsed;
                rowAlterId   .Visibility = isVmess                   ? Visibility.Visible : Visibility.Collapsed;
                rowPath      .Visibility = (hasWs || net == "grpc")  ? Visibility.Visible : Visibility.Collapsed;
                rowWsHost    .Visibility = hasWs                     ? Visibility.Visible : Visibility.Collapsed;
                rowSni       .Visibility = hasTls                    ? Visibility.Visible : Visibility.Collapsed;
                rowFp        .Visibility = hasTls                    ? Visibility.Visible : Visibility.Collapsed;
                rowAllowInsecure.Visibility = hasTls                ? Visibility.Visible : Visibility.Collapsed;
                rowPk        .Visibility = hasReality                ? Visibility.Visible : Visibility.Collapsed;
                rowSid       .Visibility = hasReality                ? Visibility.Visible : Visibility.Collapsed;
                rowSpx       .Visibility = hasReality                ? Visibility.Visible : Visibility.Collapsed;
                rowFlow      .Visibility = isVless                   ? Visibility.Visible : Visibility.Collapsed;
            }

            cmbProtocol.SelectionChanged += (_, _) => UpdateVisibility();
            cmbNetwork .SelectionChanged += (_, _) => UpdateVisibility();
            cmbSecurity.SelectionChanged += (_, _) => UpdateVisibility();
            UpdateVisibility();

            var form = new StackPanel
            {
                Spacing  = 10,
                Children =
                {
                    txtName, txtHost, numPort, cmbProtocol,
                    rowEncryption, rowPassword, rowUuid, rowAlterId,
                    cmbNetwork, rowPath, rowWsHost,
                    cmbSecurity, rowSni, rowFp, rowAllowInsecure, rowPk, rowSid, rowSpx, rowFlow
                }
            };

            var scrollViewer = new ScrollViewer
            {
                Content          = form,
                MaxHeight        = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dialog = CreateDialog();
            dialog.Title             = existing == null ? "手动添加服务器" : "编辑服务器";
            dialog.PrimaryButtonText = "保存";
            dialog.CloseButtonText   = "取消";
            dialog.DefaultButton     = ContentDialogButton.Primary;
            dialog.Content           = scrollViewer;

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var entry = existing ?? new ServerEntry();
            entry.Name        = txtName.Text.Trim();
            entry.Host        = txtHost.Text.Trim();
            entry.Port        = (int)numPort.Value;
            entry.Protocol    = cmbProtocol.SelectedItem?.ToString() ?? "ss";
            entry.Encryption  = txtEncryption.Text.Trim();
            entry.Password    = txtPassword.Password.Trim();
            entry.Uuid        = txtUuid.Text.Trim();
            entry.AlterId     = (int)numAlterId.Value;
            entry.Network     = cmbNetwork.SelectedItem?.ToString() ?? "tcp";
            entry.Path        = txtPath.Text.Trim();
            entry.WsHost      = txtWsHost.Text.Trim();
            entry.Security    = cmbSecurity.SelectedItem?.ToString() ?? "none";
            entry.Sni         = txtSni.Text.Trim();
            entry.Fingerprint = txtFp.Text.Trim();
            entry.AllowInsecure = chkAllowInsecure.IsChecked == true;
            entry.PublicKey   = txtPk.Text.Trim();
            entry.ShortId     = txtSid.Text.Trim();
            entry.SpiderX     = txtSpx.Text.Trim();
            entry.Flow        = txtFlow.Text.Trim();

            return entry;
        }

        // ── Edit local port ───────────────────────────────────────────────────

        public async Task<int?> ShowEditPortDialogAsync(int currentPort)
        {
            var numBox = new NumberBox
            {
                Header                  = "本地端口",
                Value                   = currentPort,
                Minimum                 = 1024,
                Maximum                 = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };

            var dialog = CreateDialog();
            dialog.Title             = "编辑本地端口";
            dialog.PrimaryButtonText = "确定";
            dialog.CloseButtonText   = "取消";
            dialog.DefaultButton     = ContentDialogButton.Primary;
            dialog.Content           = new StackPanel
            {
                Width    = 260,
                Spacing  = 8,
                Children =
                {
                    numBox,
                    new TextBlock
                    {
                        Text    = $"有效范围：{numBox.Minimum} - {numBox.Maximum}",
                        Opacity = 0.65,
                    }
                }
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            return double.IsNaN(numBox.Value) ? currentPort : (int)numBox.Value;
        }

        // ── Error ─────────────────────────────────────────────────────────────

        public async Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false)
        {
            var content = new TextBlock
            {
                Text        = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth    = 280
            };

            var dialog = CreateDialog();
            dialog.Title             = title;
            dialog.Content           = content;
            dialog.PrimaryButtonText = confirmText;
            dialog.CloseButtonText   = cancelText;
            dialog.DefaultButton     = isDanger ? ContentDialogButton.None : ContentDialogButton.Primary;

            if (isDanger && Application.Current.Resources.TryGetValue("DangerAccentButtonStyle", out var style) && style is Style buttonStyle)
                dialog.PrimaryButtonStyle = buttonStyle;

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        public async Task ShowErrorAsync(string title, string message)
        {
            var dialog = CreateDialog();
            dialog.Title           = title;
            dialog.Content         = message;
            dialog.CloseButtonText = "确定";
            await dialog.ShowAsync();
        }

        // ── Share link ────────────────────────────────────────────────────────

        public async Task ShowShareLinkDialogAsync(string serverName, string link)
        {
            var dialog = CreateDialog();

            // ── X close button ────────────────────────────────────────────────
            var closeBtn = new Button
            {
                Content           = "\uE711",
                FontFamily        = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Width             = 32,
                Height            = 32,
                Padding           = new Thickness(0),
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
                Text              = "分享节点",
                FontSize          = 20,
                FontWeight        = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(closeBtn,  1);
            header.Children.Add(titleText);
            header.Children.Add(closeBtn);

            // ── Link box ──────────────────────────────────────────────────────
            var linkBox = new TextBox
            {
                Text          = link,
                IsReadOnly    = true,
                TextWrapping  = TextWrapping.Wrap,
                AcceptsReturn = false,
            };


            // ── Name row (server name + animated copy icon button) ────────────
            var nameCopyBtn = new Button
            {
                Content           = "\uE8C8",
                FontFamily        = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                Width             = 28,
                Height            = 28,
                Padding           = new Thickness(0),
                FontSize          = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var subtleStyle2))
                nameCopyBtn.Style = (Style)subtleStyle2;
            ToolTipService.SetToolTip(nameCopyBtn, "复制链接");

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
                Text              = serverName,
                FontSize          = 12,
                Opacity           = 0.65,
                TextWrapping      = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText,    0);
            Grid.SetColumn(nameCopyBtn, 1);
            nameRow.Children.Add(nameText);
            nameRow.Children.Add(nameCopyBtn);

            // ── Assemble: no dialog.Title → title area collapses
            //              no CloseButtonText → bottom bar hidden
            dialog.Content = new StackPanel
            {
                Width   = 360,
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

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a ContentDialog pre-wired with the correct XamlRoot and theme.
        /// Use object-initializer syntax to set the remaining properties.
        /// </summary>
        private ContentDialog CreateDialog() => new ContentDialog
        {
            XamlRoot       = XamlRoot,
            RequestedTheme = ThemeHelper.ActualTheme,
        };

        private static FrameworkElement Wrap(FrameworkElement child) =>
            new Border { Child = child };
    }
}
