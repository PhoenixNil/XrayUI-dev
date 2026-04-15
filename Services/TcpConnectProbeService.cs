using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    public sealed class TcpConnectProbeService
    {
        public async Task<LatencyProbeResult> ProbeAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                };
            }

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await client.ConnectAsync(host, port, linkedCts.Token);
                stopwatch.Stop();

                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Success,
                    Milliseconds = (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds)
                };
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Timeout
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SocketException)
            {
                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                };
            }
            catch (Exception)
            {
                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                };
            }
        }
    }
}
