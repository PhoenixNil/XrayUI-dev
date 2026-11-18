using System;

namespace XrayUI.Services
{
    /// <summary>Tracks only the system proxy set by this process.</summary>
    internal sealed class SystemProxyOwnership
    {
        private string? _proxyServer;

        public bool HasProxy => _proxyServer is not null;

        public void Record(string proxyServer) => _proxyServer = proxyServer;

        public void ClearIfOwned(string? currentProxyServer, Action clearProxy)
        {
            if (_proxyServer is null) return;

            if (string.Equals(currentProxyServer, _proxyServer, StringComparison.OrdinalIgnoreCase))
                clearProxy();

            // An address change gives up ownership. A failed clear throws before this
            // assignment, keeping the record so shutdown can retry.
            _proxyServer = null;
        }
    }
}
