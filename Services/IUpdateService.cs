using System;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public sealed record UpdateStaging(
        string ExtractedDir,
        string RunnerExePath,
        string InstallDir,
        Version NewVersion);

    public interface IUpdateService
    {
        /// <summary>
        /// Returns null when no update is available, the architecture has no matching
        /// release asset, the .sha256 sidecar is missing, or the running build is dev
        /// (per <see cref="Helpers.AppVersion.IsDevBuild"/>).
        /// </summary>
        Task<UpdateInfo?> CheckAsync(string? proxyUrl, CancellationToken ct);

        /// <summary>
        /// Downloads the zip + .sha256, verifies the hash, extracts to a staging
        /// directory, and validates the extracted exe's FileVersion. Stages a copy
        /// of the currently installed XrayUI.Updater.exe so it can run while the
        /// install dir is being overwritten. Throws on any verification failure
        /// without touching the install dir.
        /// </summary>
        Task<UpdateStaging> DownloadVerifyAndExtractAsync(
            UpdateInfo info, string? proxyUrl, IProgress<ProgressDialogUpdate> progress, CancellationToken ct);

        /// <summary>
        /// Spawns the staged updater with handoff arguments. Caller is responsible
        /// for shutting the app down (via <c>App.RequestShutdown</c>) immediately after.
        /// </summary>
        void LaunchUpdater(UpdateStaging staging);

        /// <summary>
        /// Removes leftover staging directories under
        /// <c>%LocalAppData%\XrayUI\Updates</c>. Best-effort, non-throwing; a
        /// still-locked runner may remain until the next startup.
        /// </summary>
        void CleanupOldStagingDirs();
    }
}
