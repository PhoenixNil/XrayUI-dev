using System.Text.Json;
using XrayUI.Models;

namespace XrayUI.Tests
{
    public class SubscriptionRefreshScheduleTests
    {
        public static TheoryData<int> EnabledIntervals => new()
        {
            60,
            360,
            720,
            1440,
        };

        [Fact]
        public void AllowedIntervals_AreExactlyTheSupportedChoices()
        {
            Assert.Equal(
                new[] { 0, 60, 360, 720, 1440 },
                SubscriptionRefreshSchedule.AllowedIntervalsMinutes);
            Assert.Equal(0, SubscriptionRefreshSchedule.NormalizeInterval(30));
            Assert.Equal(0, SubscriptionRefreshSchedule.GetIntervalAt(-1));
            Assert.Equal(0, SubscriptionRefreshSchedule.GetIntervalAt(99));
        }

        [Theory]
        [MemberData(nameof(EnabledIntervals))]
        public void IsDue_UsesTheConfiguredBoundary(int intervalMinutes)
        {
            var lastAttempt = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
            var dueAt = lastAttempt.AddMinutes(intervalMinutes);

            Assert.False(SubscriptionRefreshSchedule.IsDue(
                intervalMinutes, lastAttempt, dueAt.AddTicks(-1)));
            Assert.True(SubscriptionRefreshSchedule.IsDue(
                intervalMinutes, lastAttempt, dueAt));
            Assert.Equal(
                dueAt,
                SubscriptionRefreshSchedule.GetNextRefreshAt(intervalMinutes, lastAttempt));
        }

        [Fact]
        public void DisabledOrUnanchoredSchedule_IsNotDue()
        {
            var now = DateTimeOffset.UtcNow;

            Assert.False(SubscriptionRefreshSchedule.IsDue(0, now.AddDays(-2), now));
            Assert.False(SubscriptionRefreshSchedule.IsDue(60, null, now));
            Assert.Null(SubscriptionRefreshSchedule.GetNextRefreshAt(0, now));
            Assert.Null(SubscriptionRefreshSchedule.GetNextRefreshAt(60, null));
        }

        [Fact]
        public void NewAttemptAnchor_DefersTheNextRunAfterManualRefreshOrFailure()
        {
            var priorAttempt = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
            var now = priorAttempt.AddHours(7);

            Assert.True(SubscriptionRefreshSchedule.IsDue(360, priorAttempt, now));
            Assert.False(SubscriptionRefreshSchedule.IsDue(360, now, now));
            Assert.True(SubscriptionRefreshSchedule.IsDue(360, now, now.AddHours(6)));
        }

        [Fact]
        public void MissingFieldsFromOldJson_DefaultToDisabled()
        {
            var entry = JsonSerializer.Deserialize<SubscriptionEntry>(
                """{"Name":"Legacy","Url":"https://example.com/sub"}""");

            Assert.NotNull(entry);
            Assert.Equal(0, entry.AutoRefreshIntervalMinutes);
            Assert.Null(entry.LastRefreshAttempt);
            Assert.False(entry.IsAutoRefreshEnabled);
        }

        [Fact]
        public void ScheduleFields_RoundTripThroughJson()
        {
            var attemptedAt = new DateTimeOffset(2026, 7, 26, 12, 34, 56, TimeSpan.Zero);
            var original = new SubscriptionEntry
            {
                Name = "Scheduled",
                Url = "https://example.com/sub",
                AutoRefreshIntervalMinutes = 720,
                LastRefreshAttempt = attemptedAt,
            };

            var json = JsonSerializer.Serialize(original);
            var restored = JsonSerializer.Deserialize<SubscriptionEntry>(json);

            Assert.NotNull(restored);
            Assert.Equal(720, restored.AutoRefreshIntervalMinutes);
            Assert.Equal(attemptedAt, restored.LastRefreshAttempt);
            Assert.True(restored.IsAutoRefreshEnabled);
        }
    }
}
