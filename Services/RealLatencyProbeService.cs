using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    /// <summary>
    /// "Real delay" latency test (v2rayN-style): routes a real HTTP request through each server
    /// and times the round trip. Spins up a dedicated throwaway xray core with its own ports and
    /// config — completely separate from the live connection owned by <see cref="XrayService"/> —
    /// so it works even when nothing is connected and never disturbs the user's active session.
    ///
    /// Chain servers are not supported here (they need a second outbound + proxySettings); the
    /// caller must filter them out before calling.
    /// </summary>
    public sealed class RealLatencyProbeService
    {
        private readonly SettingsService _settings;
        private readonly TunService _tunService;

        /// <summary>
        /// Live "a TUN session currently owns the default route" check, wired by the composition
        /// root (same callback pattern as ControlPanelViewModel.GetSelectedServer). Preferred over
        /// settings.IsTunMode, which is persisted runtime state that lags the UI toggle and can
        /// survive a crash as a stale true.
        /// </summary>
        public Func<bool> IsTunActive { get; set; } = () => false;

        public RealLatencyProbeService(SettingsService settings, TunService tunService)
        {
            _settings = settings;
            _tunService = tunService;
        }

        // HTTPS on purpose. Some CDN/Worker-fronted nodes never relay plaintext HTTP to a domain
        // target (and SOCKS5 here hands the core the hostname, not an IP), so the http:// form of
        // this URL timed out on every node of such a subscription even though they browse fine.
        // Same URL as mihomo's default health check.
        private const string TestUrl = "https://www.gstatic.com/generate_204";

        // Overall budget for one server's whole warm-up-then-measure cycle. 7s (vs v2rayN's 10s):
        // a usable node returns its 204 well under this, and the shorter ceiling caps how long
        // silent black-hole nodes stall the batch tail.
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(7);

        // Probe each server twice on a *reused* (keep-alive) connection and keep the fastest:
        // the first request pays the one-time tunnel + TLS handshake (very pricey for reality),
        // the second rides the warm connection and reflects steady-state RTT (≈ a TCP ping).
        // This is exactly what v2rayN's GetRealPingTime does, so the numbers line up with it.
        private const int ProbeIterations = 2;
        private const int InterProbeDelayMs = 100;

        // Up to 32 in-flight probes through the single test core. Comfortably safe — still more
        // conservative than v2rayN (which runs a whole page concurrently with no per-item cap),
        // and xray (Go) / .NET handle it trivially; the warm-up-take-min above keeps measurements
        // stable even under brief contention. Halves the wave count vs 16 for dozens of nodes.
        private const int MaxConcurrency = 32;

        // Upper bound on waiting for the throwaway core's readiness line (printed after every
        // socks inbound is bound — see XrayReadySignal). Normally arrives in tens of ms; the
        // cap reproduces the old fixed-delay behavior if the core stays silent.
        private static readonly TimeSpan CoreReadyCap = TimeSpan.FromSeconds(2);

        // Dedicated config path, separate from XrayService's xray_config.json so a test never
        // clobbers the live session's config file.
        private static readonly string ConfigPath = AppPaths.XraySpeedtestConfigPath;

        /// <summary>Populated when the test core fails to start; empty on success.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// Probes every server in <paramref name="servers"/> and reports each result through
        /// <paramref name="onResult"/> (latency in ms, or -1 for timeout/failure) as it lands.
        /// The HTTP timing runs off the UI thread for accuracy; <paramref name="onResult"/> is
        /// marshalled back to the calling (UI) thread, so it may touch bound state directly.
        /// </summary>
        public async Task ProbeAllAsync(
            IReadOnlyList<ServerEntry> servers,
            Action<ServerEntry, int> onResult,
            CancellationToken ct = default)
        {
            LastError = string.Empty;
            if (servers.Count == 0)
                return;

            // Capture the caller's context (the UI thread) so results marshal back to it for safe
            // binding updates, while the timing itself runs off-thread (ConfigureAwait(false)) —
            // UI-thread scheduling delay must not inflate the measured RTT.
            var ui = SynchronizationContext.Current;
            void Report(ServerEntry s, int ms)
            {
                if (ui is not null)
                    ui.Post(_ => onResult(s, ms), null);
                else
                    onResult(s, ms);
            }

            if (!File.Exists(XrayService.ExePath))
            {
                LastError = Loc.Format("Xray_ExeNotFound", XrayService.ExePath);
                foreach (var s in servers)
                    Report(s, -1);
                return;
            }

            // 1. Allocate one free loopback port per server.
            var entries = new List<(ServerEntry server, int port)>(servers.Count);
            foreach (var s in servers)
                entries.Add((s, LoopbackPort.PickFree()));

            // 2. Bind the throwaway core to the physical egress while TUN owns the default
            // route. autoOutboundsInterface belongs to the live core's TUN inbound and does not
            // protect this second xray process, so its proxy outbounds need an explicit sockopt.
            var settings = await _settings.LoadSettingsAsync().ConfigureAwait(false);
            var outboundInterface = IsTunActive()
                ? _tunService.ResolveOutboundInterface(settings.TunOutboundInterface)
                : null;
            Debug.WriteLine($"[RealLatencyProbe] 测速出口网卡: {outboundInterface ?? "系统默认路由"}");

            // 3. Build + write the dedicated multi-inbound speed-test config.
            string configJson = XrayConfigBuilder.BuildSpeedtestConfig(entries, outboundInterface);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            await File.WriteAllTextAsync(ConfigPath, configJson, ct).ConfigureAwait(false);

            Process? process = null;
            JobObjectGuard? jobGuard = null;
            var output = new StringBuilder();

            try
            {
                // 4. Launch the throwaway core.
                var psi = new ProcessStartInfo
                {
                    FileName = XrayService.ExePath,
                    Arguments = $"run -config \"{ConfigPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(XrayService.ExePath)!,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = XrayService.RulesDir;

                process = new Process { StartInfo = psi };
                var readySignal = XrayReadySignal.Attach(process);
                // xray logs to both stdout and stderr; capture both so a failed start has a reason.
                void Capture(string? line)
                {
                    if (line is null) return;
                    lock (output) output.AppendLine(line);
                }
                process.OutputDataReceived += (_, e) => Capture(e.Data);
                process.ErrorDataReceived += (_, e) => Capture(e.Data);

                process.Start();
                jobGuard = JobObjectGuard.Assign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 5. Wait until the core reports every inbound is up. An early exit falls
                // through to the HasExited check below, which reads the captured log.
                await readySignal.WaitAsync(CoreReadyCap, ct).ConfigureAwait(false);

                if (process.HasExited)
                {
                    string log;
                    lock (output) log = output.ToString().Trim();
                    LastError = log.Length > 0
                        ? log
                        : Loc.Format("Xray_ExitedImmediately", process.ExitCode);
                    foreach (var s in servers)
                        Report(s, -1);
                    return;
                }

                // 6. Probe each server through its own socks port (capped concurrency).
                using var throttle = new SemaphoreSlim(MaxConcurrency);
                var tasks = new List<Task>(entries.Count);
                foreach (var (server, port) in entries)
                    tasks.Add(ProbeOneAsync(server, port, throttle, Report, ct));

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                foreach (var s in servers)
                    Report(s, -1);
            }
            finally
            {
                // 7. Tear down the throwaway core and its temp config.
                try
                {
                    if (process is not null && !process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }

                jobGuard?.Dispose();
                process?.Dispose();

                try
                {
                    if (File.Exists(ConfigPath))
                        File.Delete(ConfigPath);
                }
                catch { }
            }
        }

        private static async Task ProbeOneAsync(
            ServerEntry server,
            int port,
            SemaphoreSlim throttle,
            Action<ServerEntry, int> report,
            CancellationToken ct)
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                    UseProxy = true
                };
                using var client = new HttpClient(handler);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ProbeTimeout);

                // Warm up, then measure: reuse the same client so later requests ride the
                // kept-alive connection (no repeated tunnel/TLS handshake). Keep the fastest —
                // that's the steady-state RTT, mirroring v2rayN's real-ping.
                var best = -1;
                for (var i = 0; i < ProbeIterations; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    using (await client.GetAsync(TestUrl, timeoutCts.Token).ConfigureAwait(false))
                    {
                        stopwatch.Stop();
                    }

                    var ms = (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
                    if (ms >= 0 && (best < 0 || ms < best))
                        best = ms;

                    if (i < ProbeIterations - 1)
                        await Task.Delay(InterProbeDelayMs, timeoutCts.Token).ConfigureAwait(false);
                }

                report(server, best);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Timeout, connection refused, proxy/handshake failure → unreachable.
                report(server, -1);
            }
            finally
            {
                throttle.Release();
            }
        }
    }
}
