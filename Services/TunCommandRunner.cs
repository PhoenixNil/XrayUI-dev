using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services
{
    /// <summary>Runs trusted cleanup commands with one UAC prompt and a bounded execution time.</summary>
    internal static class TunCommandRunner
    {
        internal static bool Run(IReadOnlyList<string> commandLines, bool elevate, Action<string> log)
        {
            if (commandLines.Count == 0)
                return true;

            // Continue after an error, while retaining failures from earlier commands.
            var batch = "set \"_XRAYUI_TUN_FAILED=0\" & " + string.Join(" & ", commandLines.Select(
                (command, i) => $"({command}) || (echo [TUN {i + 1}/{commandLines.Count}] exit=!errorlevel! 1>&2 & set \"_XRAYUI_TUN_FAILED=1\")"))
                + " & exit /b !_XRAYUI_TUN_FAILED!";
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /q /v:on /c " + batch,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = elevate,
                Verb = elevate ? "runas" : string.Empty,
                CreateNoWindow = !elevate,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate,
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                    return false;

                // The five-second execution budget starts after any UAC prompt is answered.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var output = elevate
                        ? Task.FromResult((StandardOutput: string.Empty, StandardError: string.Empty))
                        : process.ReadAllTextAsync(timeout.Token);
                    // Both BCL operations run without needing the caller's UI dispatcher.
                    Task.WhenAll(output, process.WaitForExitAsync(timeout.Token))
                        .WaitAsync(timeout.Token).GetAwaiter().GetResult();
                    var captured = output.GetAwaiter().GetResult();
                    log($"[TUN] fallback cleanup: exit={process.ExitCode}, elevated={elevate}");
                    foreach (var (name, text) in new[] { ("stdout", captured.StandardOutput), ("stderr", captured.StandardError) })
                    {
                        using var reader = new StringReader(text);
                        while (reader.ReadLine() is { } line)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                                log($"[TUN] {name}: {line}");
                        }
                    }
                    return process.ExitCode == 0;
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                            if (!process.WaitForExit(1000))
                                log("[TUN] cleanup process termination was not confirmed.");
                        }
                        catch (Exception ex)
                        {
                            // A non-admin parent may not be able to terminate a runas child.
                            log("[TUN] could not terminate cleanup process: " + ex.Message);
                        }
                    }
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                log("[TUN] cleanup elevation canceled.");
            }
            catch (OperationCanceledException)
            {
                log("[TUN] cleanup timed out after 5 seconds.");
            }
            catch (Exception ex)
            {
                log("[TUN] cleanup failed: " + ex.Message);
            }
            return false;
        }
    }
}
