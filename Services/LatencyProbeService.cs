using System;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public enum LatencyProbeStatus
    {
        Success,
        Timeout,
        Failed
    }

    public sealed class LatencyProbeResult
    {
        public LatencyProbeStatus Status { get; init; }

        public int? Milliseconds { get; init; }
    }

    public sealed class LatencyProbeService
    {
        private readonly TcpConnectProbeService _tcpConnectProbe;
        private readonly PingProbeService _pingProbe;

        public LatencyProbeService(
            TcpConnectProbeService tcpConnectProbe,
            PingProbeService pingProbe)
        {
            _tcpConnectProbe = tcpConnectProbe;
            _pingProbe = pingProbe;
        }

        public Task<LatencyProbeResult> ProbeAsync(
            ServerEntry server,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (server is null)
            {
                return Task.FromResult(new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                });
            }

            return string.Equals(server.Protocol, "hysteria2", StringComparison.OrdinalIgnoreCase)
                ? _pingProbe.ProbeAsync(server.Host, timeout, cancellationToken)
                : _tcpConnectProbe.ProbeAsync(server.Host, server.Port, timeout, cancellationToken);
        }
    }
}
