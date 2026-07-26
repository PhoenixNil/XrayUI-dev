using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models;

namespace XrayUI.Services
{
    public interface IDialogService
    {
        Task<string?> ShowImportLinkDialogAsync();
        Task<SubscriptionEntry?> ShowSubscriptionsDialogAsync(ManageSubscriptionsViewModel vm);
        Task<ServerEntry?> ShowEditServerDialogAsync(ServerEntry? existing);
        Task<ServerEntry?> ShowChainProxyDialogAsync(IEnumerable<ServerEntry> servers, ServerEntry? existing = null);
        Task<(int port, bool allowLan)?> ShowEditPortDialogAsync(int currentPort, bool currentAllowLan);
        Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null);
        Task<bool> ShowConfirmationAsync(string title, string message, string? confirmText = null, string? cancelText = null, bool isDanger = false);
        /// <summary>
        /// Shows the TUN confirmation dialog. Mutates <paramref name="settings"/>.TunMtu,
        /// <paramref name="settings"/>.TunOutboundInterface, and <paramref name="settings"/>.TunIpv6Enabled
        /// in-place on confirm. Returns true if the user confirmed (caller must persist), false if cancelled.
        /// </summary>
        Task<bool> ShowTunConfirmationDialogAsync(AppSettings settings);
        Task ShowShareLinkDialogAsync(string serverName, string link);
        Task<(bool enabled, bool autoConnect)?> ShowStartupDialogAsync(bool currentEnabled, bool currentAutoConnect);

        /// <summary>
        /// Confirmation shown before an app update starts. Returns true when the
        /// user chose to update now.
        /// </summary>
        /// <param name="notes">
        /// Release notes to show in the dialog body. Empty leaves the dialog a
        /// compact title + buttons confirm.
        /// </param>
        Task<bool> ShowUpdateConfirmDialogAsync(
            Version newVersion, IReadOnlyList<ChangelogEntry> notes);

        /// <summary>
        /// Shows a modal dialog with a progress bar + status text while <paramref name="work"/> runs.
        /// When progress percent is null, the progress bar is indeterminate.
        /// </summary>
        /// <param name="xamlRoot">Override which window the dialog is rooted in. Null = MainWindow.</param>
        Task ShowProgressBarDialogAsync(string title, Func<IProgress<ProgressDialogUpdate>, CancellationToken, Task> work, XamlRoot? xamlRoot = null);

        /// <summary>
        /// Shows the DNS settings dialog. Mutates <paramref name="settings"/> in-place on save.
        /// Returns true if the user saved, false if cancelled.
        /// </summary>
        /// <param name="isTunMode">Live UI TUN-mode state (not <c>settings.IsTunMode</c>, which is
        /// the persisted runtime state and lags behind the UI toggle until the next connect).
        /// Used to gate FakeDNS availability in the dialog.</param>
        Task<bool> ShowDnsSettingsDialogAsync(AppSettings settings, bool isTunMode);

        /// <summary>
        /// Shows the hotkey recorder dialog for setting/editing a global hotkey. Returns null if
        /// cancelled. Otherwise <c>cleared: true</c> means the user chose to remove the hotkey
        /// entirely; <c>cleared: false</c> carries the captured (or unchanged) combo. This dialog
        /// has no Win32 knowledge — the caller is responsible for probing/registering with
        /// user32 and only persisting on success.
        /// </summary>
        Task<(bool cleared, uint mods, uint vk)?> ShowHotkeyRecorderDialogAsync(string title, uint currentMods, uint currentVk);
    }
}
