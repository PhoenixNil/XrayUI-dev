using System.Collections.Concurrent;
using XrayUI.Services;

namespace XrayUI.Tests
{
    public class SubscriptionRefreshBatchRunnerTests
    {
        [Fact]
        public async Task RunAsync_CapsConcurrentRefreshesAtThree()
        {
            using var runner = new SubscriptionRefreshBatchRunner();
            var active = 0;
            var maxActive = 0;
            var stateLock = new object();

            Task RunRange(int start) =>
                runner.RunAsync(
                    Enumerable.Range(start, 6),
                    async _ =>
                    {
                        lock (stateLock)
                        {
                            active++;
                            maxActive = Math.Max(maxActive, active);
                        }

                        await Task.Delay(20);

                        lock (stateLock)
                        {
                            active--;
                        }
                    });

            await Task.WhenAll(RunRange(0), RunRange(100));

            Assert.Equal(SubscriptionRefreshBatchRunner.MaxConcurrency, maxActive);
            Assert.Equal(0, active);
        }

        [Fact]
        public async Task RunAsync_IsolatesFailuresAndCompletesProgress()
        {
            using var runner = new SubscriptionRefreshBatchRunner();
            var attempted = new ConcurrentBag<int>();
            var failed = new ConcurrentBag<int>();
            var progress = new ConcurrentBag<(int Completed, int Total)>();

            await runner.RunAsync(
                Enumerable.Range(0, 5),
                item =>
                {
                    attempted.Add(item);
                    return item == 2
                        ? Task.FromException(new InvalidOperationException("provider failed"))
                        : Task.CompletedTask;
                },
                (completed, total) => progress.Add((completed, total)),
                (item, _) =>
                {
                    failed.Add(item);
                    return Task.CompletedTask;
                });

            Assert.Equal(Enumerable.Range(0, 5), attempted.Order());
            Assert.Equal(new[] { 2 }, failed.Order());
            Assert.Equal(5, progress.Count);
            Assert.Contains((5, 5), progress);
        }

        [Fact]
        public async Task RunAsync_EmptyBatchDoesNothing()
        {
            using var runner = new SubscriptionRefreshBatchRunner();
            var refreshCalled = false;
            var progressCalled = false;

            await runner.RunAsync(
                Array.Empty<int>(),
                _ =>
                {
                    refreshCalled = true;
                    return Task.CompletedTask;
                },
                (_, _) => progressCalled = true);

            Assert.False(refreshCalled);
            Assert.False(progressCalled);
        }
    }
}
