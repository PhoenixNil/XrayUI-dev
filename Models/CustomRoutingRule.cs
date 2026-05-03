using System.Text.Json.Serialization;

namespace XrayUI.Models
{
    public class CustomRoutingRule
    {
        /// <summary>"domain" | "ip"</summary>
        public string Type { get; set; } = "domain";

        /// <summary>youtube.com / 192.168.0.0/16 / geosite:cn / geoip:cn</summary>
        public string Match { get; set; } = "";

        /// <summary>"proxy" | "direct" | "block"</summary>
        public string OutboundTag { get; set; } = "proxy";

        public bool IsEnabled { get; set; } = true;

        // Helpers for x:Bind (OneTime) inside DataTemplate.
        // Visibility is computed directly to avoid converter lookups in a Window root.
        [JsonIgnore] public Visibility DomainVisibility => Type == "domain" ? Visibility.Visible : Visibility.Collapsed;
        [JsonIgnore] public Visibility IpVisibility     => Type == "ip"     ? Visibility.Visible : Visibility.Collapsed;

        public CustomRoutingRule Clone() => new()
        {
            Type        = Type,
            Match       = Match,
            OutboundTag = OutboundTag,
            IsEnabled   = IsEnabled,
        };
    }
}
