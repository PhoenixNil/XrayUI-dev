using System.Threading.Tasks;
using XrayUI.Models;
using XrayUI.ViewModels;

namespace XrayUI.Services
{
    public interface IDialogService
    {
        Task<string?> ShowImportLinkDialogAsync();
        Task<SubscriptionEntry?> ShowSubscriptionsDialogAsync(ManageSubscriptionsViewModel vm);
        Task<ServerEntry?> ShowEditServerDialogAsync(ServerEntry? existing);
        Task<int?> ShowEditPortDialogAsync(int currentPort);
        Task ShowErrorAsync(string title, string message);
        Task<bool> ShowConfirmationAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false);
        Task ShowShareLinkDialogAsync(string serverName, string link);
        Task<(bool enabled, bool autoConnect)?> ShowStartupDialogAsync(bool currentEnabled, bool currentAutoConnect);
    }
}
