using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    public sealed class PingProbeService
    {
        public async Task<LatencyProbeResult> ProbeAsync(
            string host,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                };
            }

            using var ping = new Ping();

            try
            {
                using var registration = cancellationToken.Register(ping.SendAsyncCancel);
                var reply = await ping.SendPingAsync(host, (int)Math.Ceiling(timeout.TotalMilliseconds));

                return reply.Status switch
                {
                    IPStatus.Success => new LatencyProbeResult
                    {
                        Status = LatencyProbeStatus.Success,
                        Milliseconds = (int)Math.Round((double)reply.RoundtripTime)
                    },
                    IPStatus.TimedOut => new LatencyProbeResult
                    {
                        Status = LatencyProbeStatus.Timeout
                    },
                    _ => new LatencyProbeResult
                    {
                        Status = LatencyProbeStatus.Failed
                    }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PingException)
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
