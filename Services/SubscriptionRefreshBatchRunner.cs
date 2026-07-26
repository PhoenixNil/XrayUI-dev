using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    /// <summary>
    /// Runs subscription refresh callbacks with the same bounded fan-out for manual bulk updates
    /// and scheduled updates. One item failing never prevents the remaining snapshot from running.
    /// </summary>
    public sealed class SubscriptionRefreshBatchRunner : IDisposable
    {
        public const int MaxConcurrency = 3;
        private readonly SemaphoreSlim _gate = new(MaxConcurrency);
        private bool _disposed;

        /// <remarks>
        /// <paramref name="progress"/> and <paramref name="onError"/> run on whatever context the
        /// awaits below resume on — there is no ConfigureAwait here, so that is the caller's
        /// context. Every current call site starts a batch from the UI thread, which is the only
        /// reason the progress callback can assign bindable state (and so raise PropertyChanged)
        /// directly. Call this from a background thread, or add ConfigureAwait(false) below, and
        /// those callbacks land off-thread — where touching bound state throws
        /// RPC_E_WRONG_THREAD in the ViewModel, far from the change that caused it. Marshal
        /// yourself if you need that; this type stays plain net10.0 with no dispatcher dependency
        /// so it can be source-linked into the test project.
        /// </remarks>
        public async Task RunAsync<T>(
            IEnumerable<T> source,
            Func<T, Task> refresh,
            Action<int, int>? progress = null,
            Func<T, Exception, Task>? onError = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(refresh);

            var items = source.ToList();
            if (items.Count == 0) return;

            var completed = 0;
            var tasks = items.Select(async item =>
            {
                await _gate.WaitAsync();
                try
                {
                    await refresh(item);
                }
                catch (Exception ex)
                {
                    if (onError is not null)
                    {
                        try
                        {
                            await onError(item, ex);
                        }
                        catch
                        {
                            // Error reporting is best-effort. A failed settings write must not
                            // prevent the rest of the batch from refreshing.
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                    progress?.Invoke(Interlocked.Increment(ref completed), items.Count);
                }
            });

            await Task.WhenAll(tasks);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gate.Dispose();
        }
    }
}
