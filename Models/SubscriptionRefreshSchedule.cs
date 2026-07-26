using System;
using System.Collections.Generic;

namespace XrayUI.Models
{
    /// <summary>
    /// Pure scheduling policy for subscription refreshes. Keeping the supported intervals and
    /// due-time calculation here gives the WinUI timer, the edit UI and unit tests one source of
    /// truth without coupling the policy to a dispatcher or wall-clock service.
    /// </summary>
    public static class SubscriptionRefreshSchedule
    {
        private static readonly int[] Intervals = [0, 60, 360, 720, 1440];

        public static IReadOnlyList<int> AllowedIntervalsMinutes => Intervals;

        public static int NormalizeInterval(int minutes) =>
            Array.IndexOf(Intervals, minutes) >= 0 ? minutes : 0;

        // NormalizeInterval always lands on a member of Intervals, so the lookup cannot miss.
        public static int GetIndex(int minutes) =>
            Array.IndexOf(Intervals, NormalizeInterval(minutes));

        public static int GetIntervalAt(int index) =>
            (uint)index < (uint)Intervals.Length ? Intervals[index] : 0;

        /// <summary>
        /// Expressed through <see cref="GetNextRefreshAt"/> rather than recomputing the boundary,
        /// so a later policy change (jitter, an exclusive comparison, ...) cannot leave the
        /// predicate and the displayed time disagreeing.
        /// </summary>
        public static bool IsDue(
            int intervalMinutes,
            DateTimeOffset? lastRefreshAttempt,
            DateTimeOffset now) =>
            GetNextRefreshAt(intervalMinutes, lastRefreshAttempt) is { } next && next <= now;

        /// <summary>The instant <see cref="IsDue"/> starts returning true; null when no schedule
        /// is enabled or none has been anchored yet.</summary>
        public static DateTimeOffset? GetNextRefreshAt(
            int intervalMinutes,
            DateTimeOffset? lastRefreshAttempt)
        {
            var normalized = NormalizeInterval(intervalMinutes);
            return normalized > 0 && lastRefreshAttempt.HasValue
                ? lastRefreshAttempt.Value.AddMinutes(normalized)
                : null;
        }
    }
}
