using System;
using System.Collections.Generic;
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

        /// <param name="availableServers">The whole server list, read during this call. A chain
        /// is probed at its entry hop, the first connection it makes, resolved from this list.</param>
        public Task<LatencyProbeResult> ProbeAsync(
            ServerEntry server,
            IEnumerable<ServerEntry> availableServers,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (server is null
                || !XrayConfigBuilder.TryResolveHops(server, availableServers, out _, out var chainEntry))
            {
                return Task.FromResult(new LatencyProbeResult
                {
                    Status = LatencyProbeStatus.Failed
                });
            }

            var target = chainEntry ?? server;

            // hysteria2 and wireguard expose a UDP endpoint that won't answer a TCP connect, so
            // fall back to an ICMP ping of the host for their single-node latency readout.
            var isUdpEndpoint =
                string.Equals(target.Protocol, "hysteria2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target.Protocol, "wireguard", StringComparison.OrdinalIgnoreCase);

            return isUdpEndpoint
                ? _pingProbe.ProbeAsync(target.Host, timeout, cancellationToken)
                : _tcpConnectProbe.ProbeAsync(target.Host, target.Port, timeout, cancellationToken);
        }
    }
}
