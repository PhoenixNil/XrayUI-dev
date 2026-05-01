using System.Collections.Generic;

namespace XrayUI.Models
{
    public class PresetSettings
    {
        public List<SubscriptionEntry>? Subscriptions { get; set; }
        public List<CustomRoutingRule>? CustomRules { get; set; }
    }
}
