using XrayUI.Models;

namespace XrayUI.ViewModels
{
    public partial class ServerGroupChip : ObservableObject
    {
        public enum ChipKind
        {
            All,
            Subscription,
            Ungrouped,
        }

        public ChipKind Kind { get; init; }

        public string DisplayName { get; set; } = string.Empty;

        public string? SubscriptionId { get; init; }

        public SubscriptionEntry? Subscription { get; init; }
    }
}
