using System;

namespace XrayUI.Models
{
    /// <summary>
    /// Portable subscription configuration stored in a preset. Runtime state deliberately stays
    /// in the app's own settings.json: importing a preset must not restore stale update timestamps,
    /// provider errors, quota figures or an automatic background-refresh policy.
    /// </summary>
    public sealed class SubscriptionPresetEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        public static SubscriptionPresetEntry FromSubscription(SubscriptionEntry source)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new SubscriptionPresetEntry
            {
                Id = source.Id,
                Name = source.Name,
                Url = source.Url,
            };
        }

        public SubscriptionEntry ToSubscription() => new()
        {
            Id = Id,
            Name = Name,
            Url = Url,
        };
    }
}
