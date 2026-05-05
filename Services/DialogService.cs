using System;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
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
  
        // ── Subscriptions ─────────────────────────────────────────────────────

        public async Task<SubscriptionEntry?> ShowSubscriptionsDialogAsync(ManageSubscriptionsViewModel vm)
        {
            var dialog = CreateDialog();
            dialog.Content = new ManageSubscriptionsDialog(vm);

            void SyncDialogButtons()
            {
                if (vm.IsAddPage)
                {
                    dialog.PrimaryButtonText      = "添加";
                    dialog.CloseButtonText        = "取消";
                    dialog.DefaultButton          = ContentDialogButton.Primary;
                    dialog.IsPrimaryButtonEnabled = vm.CanAddSubscription;
                    return;
                }

                dialog.PrimaryButtonText      = string.Empty;
                dialog.CloseButtonText        = "完成";
                dialog.DefaultButton          = ContentDialogButton.Close;
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
            var txtName     = new TextBox { Header = "名称", Text = existing?.Name ?? string.Empty, MinWidth = 420 };
            var txtHost     = new TextBox { Header = "地址 / 域名", Text = existing?.Host ?? string.Empty };
            var numPort     = new NumberBox { Header = "端口", Value = existing?.Port ?? 443, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            var cmbProtocol = new ComboBox  { Header = "协议", MinWidth = 200 };
            foreach (var p in new[] { "ss", "vmess", "vless", "hysteria2", "trojan" })
                cmbProtocol.Items.Add(p);
            cmbProtocol.SelectedItem = existing?.Protocol?.ToLower() ?? "ss";

            var cmbEncryption = new ComboBox { Header = "加密方式 (SS)", MinWidth = 200 };
            foreach (var m in new[] { "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305" })
                cmbEncryption.Items.Add(m);
            if (existing?.Encryption is { Length: > 0 } existingEnc && !cmbEncryption.Items.Contains(existingEnc))
                cmbEncryption.Items.Add(existingEnc);
            cmbEncryption.SelectedItem = existing?.Encryption ?? "aes-128-gcm";
            var txtPassword   = new PasswordBox { Header = "密码", Password = existing?.Password ?? string.Empty };
            var txtUuid       = new TextBox { Header = "UUID (VMess / VLESS)", Text = existing?.Uuid ?? string.Empty };
            var numAlterId    = new NumberBox { Header = "AlterId (VMess)", Value = existing?.AlterId ?? 0, Minimum = 0, Maximum = 65535 };
            var cmbNetwork    = new ComboBox { Header = "传输协议", MinWidth = 200 };
            foreach (var n in new[] { "tcp", "ws", "grpc", "xhttp" })
                cmbNetwork.Items.Add(n);
            cmbNetwork.SelectedItem = existing?.Network ?? "tcp";

            var txtPath     = new TextBox { Header = "路径 (WS/gRPC/XHTTP)", Text = existing?.Path ?? string.Empty };
            var txtWsHost   = new TextBox { Header = "Host 头 (WS/XHTTP)", Text = existing?.WsHost ?? string.Empty };
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
            var txtFinalmask = new TextBox
            {
                Header = "Finalmask (JSON)",
                Text = existing?.Finalmask ?? string.Empty,
                AcceptsReturn = true,
                Height = 104,
                TextWrapping = TextWrapping.NoWrap
            };

            // Row containers for conditional visibility
            var rowEncryption = Wrap(cmbEncryption);
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
            var rowFinalmask  = Wrap(txtFinalmask);

            void UpdateVisibility()
            {
                var proto = cmbProtocol.SelectedItem?.ToString() ?? "ss";
                var net   = cmbNetwork.SelectedItem?.ToString() ?? "tcp";
                var sec   = cmbSecurity.SelectedItem?.ToString() ?? "none";

                bool isSs        = proto == "ss";
                bool isVmess     = proto == "vmess";
                bool isVless     = proto == "vless";
                bool isHysteria2 = proto == "hysteria2";
                bool isTrojan    = proto == "trojan";
                bool hasWs       = !isHysteria2 && net == "ws";
                bool hasXhttp    = !isHysteria2 && net == "xhttp";
                bool hasGrpc     = !isHysteria2 && net == "grpc";
                bool hasTls      = !isHysteria2 && (sec == "tls" || sec == "reality");
                bool hasReality  = !isHysteria2 && sec == "reality";

                cmbNetwork .Visibility = isHysteria2                 ? Visibility.Collapsed : Visibility.Visible;
                cmbSecurity.Visibility = isHysteria2                 ? Visibility.Collapsed : Visibility.Visible;

                rowEncryption.Visibility = isSs                       ? Visibility.Visible : Visibility.Collapsed;
                rowPassword  .Visibility = (isSs || isHysteria2 || isTrojan)
                                                                          ? Visibility.Visible : Visibility.Collapsed;
                rowUuid      .Visibility = (isVmess || isVless)       ? Visibility.Visible : Visibility.Collapsed;
                rowAlterId   .Visibility = isVmess                    ? Visibility.Visible : Visibility.Collapsed;
                rowPath      .Visibility = (hasWs || hasXhttp || hasGrpc) ? Visibility.Visible : Visibility.Collapsed;
                rowWsHost    .Visibility = (hasWs || hasXhttp)        ? Visibility.Visible : Visibility.Collapsed;
                rowSni       .Visibility = (hasTls || isHysteria2)    ? Visibility.Visible : Visibility.Collapsed;
                rowFp        .Visibility = hasTls                     ? Visibility.Visible : Visibility.Collapsed;
                rowAllowInsecure.Visibility = (hasTls || isHysteria2) ? Visibility.Visible : Visibility.Collapsed;
                rowPk        .Visibility = hasReality                 ? Visibility.Visible : Visibility.Collapsed;
                rowSid       .Visibility = hasReality                 ? Visibility.Visible : Visibility.Collapsed;
                rowSpx       .Visibility = hasReality                 ? Visibility.Visible : Visibility.Collapsed;
                rowFlow      .Visibility = isVless                    ? Visibility.Visible : Visibility.Collapsed;
            }

            cmbProtocol.SelectionChanged += (_, _) =>
            {
                var proto = cmbProtocol.SelectedItem?.ToString();
                if ((proto == "trojan" || proto == "hysteria2")
                    && cmbSecurity.SelectedItem?.ToString() == "none")
                {
                    cmbSecurity.SelectedItem = "tls";
                }

                UpdateVisibility();
            };
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
                    cmbSecurity, rowSni, rowFp, rowAllowInsecure, rowPk, rowSid, rowSpx, rowFlow,
                    rowFinalmask
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
            entry.Encryption  = cmbEncryption.SelectedItem?.ToString() ?? string.Empty;
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
            entry.Finalmask   = FinalmaskJson.NormalizeForStorage(txtFinalmask.Text);

            if (entry.Protocol == "hysteria2")
            {
                entry.Security = "tls";
            }

            if (entry.Protocol != "ss")
            {
                entry.Encryption = entry.Security == "reality" ? "Reality"
                                 : entry.Security == "tls"     ? "TLS"
                                                               : "None";
            }

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

        public async Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null)
        {
            var dialog = CreateDialog(xamlRoot);
            dialog.Title           = title;
            dialog.Content         = message;
            dialog.CloseButtonText = "确定";
            await dialog.ShowAsync();
        }

        // ── Progress ──────────────────────────────────────────────────────────

        public async Task ShowProgressDialogAsync(string title, Func<IProgress<string>, CancellationToken, Task> work, XamlRoot? xamlRoot = null)
        {
            using var cts = new CancellationTokenSource();

            var statusText = new TextBlock
            {
                Text         = "正在准备…",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var ring = new ProgressRing
            {
                IsActive = true,
                Width    = 36,
                Height   = 36,
            };

            var dialog = CreateDialog(xamlRoot);
            dialog.Title           = title;
            dialog.CloseButtonText = "取消";
            dialog.Content = new StackPanel
            {
                Spacing             = 16,
                MinWidth            = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children            = { ring, statusText }
            };

            // Progress<T> captures the current SynchronizationContext — since we're on the UI
            // thread here, reports from the worker thread are marshalled back automatically.
            var progress = new Progress<string>(s => statusText.Text = s);

            Exception? error   = null;
            int workFinished = 0;

            dialog.Opened += (_, _) =>
            {
                if (Volatile.Read(ref workFinished) == 1)
                {
                    try { dialog.Hide(); } catch { }
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
                        try { dialog.Hide(); } catch { }
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

        public async Task ShowProgressBarDialogAsync(string title, Func<IProgress<ProgressDialogUpdate>, CancellationToken, Task> work, XamlRoot? xamlRoot = null)
        {
            using var cts = new CancellationTokenSource();

            var statusText = new TextBlock
            {
                Text         = "正在准备…",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Minimum         = 0,
                Maximum         = 100,
                Width           = 320,
            };

            var dialog = CreateDialog(xamlRoot);
            dialog.Title           = title;
            dialog.CloseButtonText = "取消";
            dialog.Content = new StackPanel
            {
                Spacing             = 12,
                MinWidth            = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children            = { progressBar, statusText }
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

            Exception? error   = null;
            int workFinished = 0;

            dialog.Opened += (_, _) =>
            {
                if (Volatile.Read(ref workFinished) == 1)
                {
                    try { dialog.Hide(); } catch { }
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
                        try { dialog.Hide(); } catch { }
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

        // ── Startup ───────────────────────────────────────────────────────────

        public async Task<(bool enabled, bool autoConnect)?> ShowStartupDialogAsync(bool currentEnabled, bool currentAutoConnect)
        {
            var toggle = new ToggleSwitch
            {
                IsOn       = currentEnabled,
                OnContent  = "开",
                OffContent = "关",
                MinWidth   = 0,
                Margin     = new Thickness(0),
            };

            var toggleLabel = new TextBlock
            {
                Text              = "开机自动启动",
                VerticalAlignment = VerticalAlignment.Center,
            };

            var toggleRow = new Grid { ColumnSpacing = 8 };
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(toggleLabel, 0);
            Grid.SetColumn(toggle,      1);
            toggleRow.Children.Add(toggleLabel);
            toggleRow.Children.Add(toggle);

            var checkBox = new CheckBox
            {
                Content   = "自动连接上次节点",
                IsChecked = currentAutoConnect,
                IsEnabled = currentEnabled,
                Margin    = new Thickness(16, 0, 0, 0),
            };

            toggle.Toggled += (_, _) => checkBox.IsEnabled = toggle.IsOn;

            var dialog = CreateDialog();
            dialog.Title             = "开机启动";
            dialog.PrimaryButtonText = "确认";
            dialog.CloseButtonText   = "取消";
            dialog.DefaultButton     = ContentDialogButton.Primary;
            dialog.Content = new StackPanel
            {
                Width    = 260,
                Spacing  = 12,
                Children = { toggleRow, checkBox },
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            return (toggle.IsOn, checkBox.IsChecked == true);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a ContentDialog pre-wired with the correct XamlRoot and theme.
        /// Use object-initializer syntax to set the remaining properties.
        /// </summary>
        /// <param name="xamlRootOverride">If supplied, roots the dialog in this window instead of the MainWindow factory.</param>
        private ContentDialog CreateDialog(XamlRoot? xamlRootOverride = null) => new ContentDialog
        {
            XamlRoot       = xamlRootOverride ?? XamlRoot,
            RequestedTheme = ThemeHelper.ActualTheme,
        };

        private static Border Wrap(FrameworkElement child) =>
            new Border { Child = child };
    }
}
