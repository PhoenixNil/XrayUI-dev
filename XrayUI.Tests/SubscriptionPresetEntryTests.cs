using System.Text.Json;
using XrayUI.Models;

namespace XrayUI.Tests
{
    public class SubscriptionPresetEntryTests
    {
        [Fact]
        public void ExportCopy_ContainsOnlyPortableSubscriptionConfiguration()
        {
            var source = new SubscriptionEntry
            {
                Id = "subscription-id",
                Name = "Provider",
                Url = "https://example.com/sub",
                LastUpdated = new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
                LastError = "stale provider error",
                Usage = new SubscriptionUserInfo(
                    Up: 1,
                    Down: 2,
                    Total: 3,
                    Expire: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
                AutoRefreshIntervalMinutes = 360,
                LastRefreshAttempt = new DateTimeOffset(2026, 7, 26, 11, 0, 0, TimeSpan.Zero),
            };

            var presetEntry = SubscriptionPresetEntry.FromSubscription(source);
            var json = JsonSerializer.Serialize(presetEntry);

            Assert.Equal("subscription-id", presetEntry.Id);
            Assert.Equal("Provider", presetEntry.Name);
            Assert.Equal("https://example.com/sub", presetEntry.Url);
            Assert.DoesNotContain(nameof(SubscriptionEntry.LastUpdated), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.LastError), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.Upload), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.Download), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.Total), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.Expire), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.AutoRefreshIntervalMinutes), json);
            Assert.DoesNotContain(nameof(SubscriptionEntry.LastRefreshAttempt), json);
        }

        [Fact]
        public void Import_IgnoresRuntimeFieldsFromOlderPreset()
        {
            var presetEntry = JsonSerializer.Deserialize<SubscriptionPresetEntry>(
                """
                {
                  "Id": "legacy-id",
                  "Name": "Legacy provider",
                  "Url": "https://example.com/legacy",
                  "LastUpdated": "2026-07-26T10:00:00+00:00",
                  "LastError": "old error",
                  "Upload": 10,
                  "Download": 20,
                  "Total": 30,
                  "Expire": "2026-08-01T00:00:00+00:00",
                  "AutoRefreshIntervalMinutes": 60,
                  "LastRefreshAttempt": "2026-07-26T11:00:00+00:00"
                }
                """);

            Assert.NotNull(presetEntry);

            var imported = presetEntry.ToSubscription();

            Assert.Equal("legacy-id", imported.Id);
            Assert.Equal("Legacy provider", imported.Name);
            Assert.Equal("https://example.com/legacy", imported.Url);
            Assert.Null(imported.LastUpdated);
            Assert.Null(imported.LastError);
            Assert.Equal(default, imported.Usage);
            Assert.Equal(0, imported.AutoRefreshIntervalMinutes);
            Assert.Null(imported.LastRefreshAttempt);
        }
    }
}
