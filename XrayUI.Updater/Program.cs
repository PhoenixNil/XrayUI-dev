using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace XrayUI.Updater;

internal static class Program
{
    private const string ParentPidArg    = "--parent-pid=";
    private const string ExtractedDirArg = "--extracted-dir=";
    private const string InstallDirArg   = "--install-dir=";
    private const string LaunchAfterArg  = "--launch-after=";
    private const string ElevatedFlag    = "--elevated";

    private const int CopyRetryCount     = 5;
    private const int CopyRetryDelayMs   = 200;
    private const int ParentExitTimeoutMs = 15_000;

    private static StreamWriter? _log;

    private static int Main(string[] args)
    {
        try
        {
            int? parentPid = null;
            string? extractedDir = null, installDir = null, launchAfter = null;
            var elevated = false;

            foreach (var a in args)
            {
                if (a.StartsWith(ParentPidArg, StringComparison.OrdinalIgnoreCase))
                    parentPid = int.TryParse(a[ParentPidArg.Length..], out var p) ? p : null;
                else if (a.StartsWith(ExtractedDirArg, StringComparison.OrdinalIgnoreCase))
                    extractedDir = a[ExtractedDirArg.Length..];
                else if (a.StartsWith(InstallDirArg, StringComparison.OrdinalIgnoreCase))
                    installDir = a[InstallDirArg.Length..];
                else if (a.StartsWith(LaunchAfterArg, StringComparison.OrdinalIgnoreCase))
                    launchAfter = a[LaunchAfterArg.Length..];
                else if (string.Equals(a, ElevatedFlag, StringComparison.OrdinalIgnoreCase))
                    elevated = true;
            }

            if (parentPid is null || string.IsNullOrEmpty(extractedDir) ||
                string.IsNullOrEmpty(installDir) || string.IsNullOrEmpty(launchAfter))
            {
                Console.Error.WriteLine("Usage: XrayUI.Updater --parent-pid=N --extracted-dir=PATH --install-dir=PATH --launch-after=NAME [--elevated]");
                return 2;
            }

            OpenLog(installDir);
            Log($"Updater started. parent={parentPid}, elevated={elevated}");
            Log($"  extracted-dir = {extractedDir}");
            Log($"  install-dir   = {installDir}");
            Log($"  launch-after  = {launchAfter}");

            WaitForParentExit(parentPid.Value);

            if (!TryEnsureWritable(installDir))
            {
                if (!elevated)
                {
                    Log("Install dir not writable; relaunching elevated…");
                    RelaunchElevated(args);
                    return 0;
                }
                Log("Install dir still not writable after elevation. Aborting.");
                return 3;
            }

            CopyOverwrite(extractedDir, installDir);
            Log("Copy complete.");

            var newExe = Path.Combine(installDir, launchAfter);
            if (!File.Exists(newExe))
            {
                Log($"New app exe not found after update: {newExe}. Aborting launch.");
                return 4;
            }

            CleanupLargeStagingDirs(extractedDir);

            // Launch the new app unelevated. Even if we elevated to do the file
            // overwrite, the app itself should run under the user's normal token.
            try
            {
                LaunchApp(newExe, installDir);
                Log("New app launched.");
            }
            catch (Exception ex)
            {
                Log($"Failed to launch new app: {ex}");
                return 5;
            }

            return 0;
        }
        catch (Exception ex)
        {
            try { Log($"Fatal: {ex}"); } catch { }
            return 1;
        }
        finally
        {
            try { _log?.Flush(); _log?.Dispose(); } catch { }
        }
    }

    private static void WaitForParentExit(int pid)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (!parent.WaitForExit(ParentExitTimeoutMs))
            {
                Log($"Parent {pid} did not exit within {ParentExitTimeoutMs} ms; continuing anyway.");
            }
        }
        catch (ArgumentException)
        {
            // Process already gone — that's fine.
        }
        catch (Exception ex)
        {
            Log($"WaitForParentExit error: {ex.Message}");
        }
    }

    private static bool TryEnsureWritable(string installDir)
    {
        try
        {
            var probe = Path.Combine(installDir, ".xrayui-write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException ex) { Log($"Write probe denied: {ex.Message}"); return false; }
        catch (IOException ex)                 { Log($"Write probe IO error: {ex.Message}"); return false; }
    }

    private static void RelaunchElevated(string[] originalArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName        = Environment.ProcessPath ?? "XrayUI.Updater.exe",
            UseShellExecute = true,
            Verb            = "runas",
        };
        foreach (var a in originalArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(ElevatedFlag);
        Process.Start(psi);
    }

    private static void CopyOverwrite(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        var copied = 0;
        string? currentFile = null;
        try
        {
            foreach (var srcFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                currentFile = Path.GetRelativePath(source, srcFile);
                var dstFile = Path.Combine(dest, currentFile);
                var dstDir  = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrEmpty(dstDir))
                    Directory.CreateDirectory(dstDir);

                CopyWithRetry(srcFile, dstFile);
                copied++;
            }
            Log($"Copied {copied} files into {dest}");
        }
        catch (Exception ex)
        {
            // We deliberately do NOT auto-rollback: it would require pre-copying every
            // existing file to a backup location (~doubling the disk footprint of the
            // upgrade) and most failures here are transient/permission issues that
            // re-running the updater (or a manual extract) can resolve. Surface a
            // detailed log so the user can see exactly what to retry.
            Log($"Copy aborted after {copied} files. Failed on '{currentFile}': {ex.Message}");
            throw;
        }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        for (var attempt = 0; attempt < CopyRetryCount; attempt++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                    && attempt < CopyRetryCount - 1)
            {
                Log($"Copy retry {attempt + 1}/{CopyRetryCount} for {dst}: {ex.Message}");
                Thread.Sleep(CopyRetryDelayMs);
            }
        }
        // Final attempt without catching — let it throw to abort the update.
        File.Copy(src, dst, overwrite: true);
    }

    private static void CleanupLargeStagingDirs(string extractedDir)
    {
        var stageRoot = Directory.GetParent(extractedDir)?.FullName;
        if (string.IsNullOrEmpty(stageRoot))
            return;

        DeleteDirectoryBestEffort(Path.Combine(stageRoot, "download"));
        DeleteDirectoryBestEffort(extractedDir);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            Directory.Delete(path, recursive: true);
            Log($"Deleted staging directory: {path}");
        }
        catch (Exception ex)
        {
            Log($"Could not delete staging directory '{path}': {ex.Message}");
        }
    }

    private static void LaunchApp(string exePath, string workingDirectory)
    {
        if (IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "explorer.exe",
                    Arguments       = QuoteForCommandLine(exePath),
                    UseShellExecute = true,
                });
                return;
            }
            catch (Exception ex)
            {
                Log($"Unelevated launch via Explorer failed: {ex.Message}");
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute  = true,
        });
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteForCommandLine(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void OpenLog(string installDir)
    {
        try
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XrayUI", "Updates");
            Directory.CreateDirectory(logRoot);
            var path = Path.Combine(logRoot, "updater.log");
            _log = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _log = null; // logging is best-effort
        }
    }

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
        try { _log?.WriteLine(line); } catch { }
        try { Console.WriteLine(line); } catch { }
    }
}
