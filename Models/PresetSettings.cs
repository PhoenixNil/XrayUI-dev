using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace XrayUI.Models
{
    public class PresetSettings
    {
        public List<SubscriptionPresetEntry>? Subscriptions { get; set; }
        public List<CustomRoutingRule>? CustomRules { get; set; }
        public JsonObject? AdvancedRouting { get; set; }
    }
}
