using System;
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
    }
}
