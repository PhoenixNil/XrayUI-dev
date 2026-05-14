using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public interface IDialogService
    {
        Task<string?> ShowImportLinkDialogAsync();
        Task<SubscriptionEntry?> ShowSubscriptionsDialogAsync(ManageSubscriptionsViewModel vm);
        Task<ServerEntry?> ShowEditServerDialogAsync(ServerEntry? existing);
        Task<ServerEntry?> ShowChainProxyDialogAsync(IEnumerable<ServerEntry> servers, ServerEntry? existing = null);
        Task<int?> ShowEditPortDialogAsync(int currentPort);
        Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null);
        Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false);
        Task ShowShareLinkDialogAsync(string serverName, string link);
        Task<(bool enabled, bool autoConnect)?> ShowStartupDialogAsync(bool currentEnabled, bool currentAutoConnect);

        /// <summary>
        /// Shows a modal dialog with a progress ring + status text while <paramref name="work"/> runs.
        /// Throws <see cref="OperationCanceledException"/> if the user cancels; rethrows any other exception from the work.
        /// </summary>
        /// <param name="xamlRoot">Override which window the dialog is rooted in. Null = MainWindow.</param>
        Task ShowProgressDialogAsync(string title, Func<IProgress<string>, CancellationToken, Task> work, XamlRoot? xamlRoot = null);

        /// <summary>
        /// Shows a modal dialog with a progress bar + status text while <paramref name="work"/> runs.
        /// When progress percent is null, the progress bar is indeterminate.
        /// </summary>
        /// <param name="xamlRoot">Override which window the dialog is rooted in. Null = MainWindow.</param>
        Task ShowProgressBarDialogAsync(string title, Func<IProgress<ProgressDialogUpdate>, CancellationToken, Task> work, XamlRoot? xamlRoot = null);

        /// <summary>
        /// Shows the DNS settings dialog. Mutates <paramref name="settings"/> in-place on save.
        /// Returns true if the user saved, false if cancelled.
        /// </summary>
        /// <param name="isTunMode">Live UI TUN-mode state (not <c>settings.IsTunMode</c>, which is
        /// the persisted runtime state and lags behind the UI toggle until the next connect).
        /// Used to gate FakeDNS availability in the dialog.</param>
        Task<bool> ShowDnsSettingsDialogAsync(AppSettings settings, bool isTunMode);
    }
}
