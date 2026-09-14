global using CommunityToolkit.Mvvm.Input;
global using Microsoft.UI.Xaml;

// The editor state machine is tested without a Windows App SDK runtime or user files.
namespace Microsoft.UI.Xaml
{
    public sealed class XamlRoot { }
}

namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
}

namespace XrayUI.Models
{
    public sealed class AppSettings
    {
        public bool UseTunConfigProfile { get; set; }
        public bool UseProxyConfigProfile { get; set; }
    }
}

namespace XrayUI.Services
{
    public sealed class SettingsService
    {
        public Models.AppSettings Settings { get; } = new();
        public Func<Task<Models.AppSettings>>? Load { get; set; }
        public Func<Models.AppSettings, Task<bool>> Save { get; set; } = _ => Task.FromResult(true);
        public Task<Models.AppSettings> LoadSettingsAsync() => Load?.Invoke() ?? Task.FromResult(Settings);
        public Task<bool> SaveSettingsAsync(Models.AppSettings settings) => Save(settings);
    }

    public sealed class ConfigProfileStore
    {
        public Func<bool, Task<string?>> Read { get; set; } = _ => Task.FromResult<string?>(null);
        public Func<bool, string, Task> Write { get; set; } = (_, _) => Task.CompletedTask;
        public Task<string?> ReadAsync(bool tunSlot) => Read(tunSlot);
        public Task WriteAsync(bool tunSlot, string text) => Write(tunSlot, text);
        public static void OpenFolder() { }
    }

    public interface IDialogService
    {
        Task<bool> ShowConfirmationAsync(string title, string message, bool isDanger = false, XamlRoot? xamlRoot = null);
        Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null);
    }

    public sealed class ProfileTestDialogs : IDialogService
    {
        public Func<Task<bool>> Confirm { get; set; } = () => Task.FromResult(true);
        public Task<bool> ShowConfirmationAsync(string title, string message, bool isDanger = false, XamlRoot? xamlRoot = null) => Confirm();
        public Task ShowErrorAsync(string title, string message, XamlRoot? xamlRoot = null) => Task.CompletedTask;
    }

    public static class XrayConfigBuilder
    {
        public const string ProxyTemplate = """{"inbounds":[{"tag":"mixed-in","protocol":"socks","port":1080}]}""";
        public const string TunTemplate = """{"inbounds":[{"tag":"tun-in","protocol":"tun","settings":{"name":"xray-tun"}}]}""";
        public static string BuildProfileTemplate(Models.AppSettings settings, bool tunSlot) => tunSlot ? TunTemplate : ProxyTemplate;
    }
}

namespace XrayUI.Helpers
{
    public static class AppPaths
    {
        public static string LocalAppDataDir => Path.GetTempPath();
        public static string XrayConfigPreviewPath => Path.Combine(LocalAppDataDir, "xray-profile-test-preview.json");
    }

    public static partial class L
    {
        public static string ConfigProfile_DiscardMsg => "Discard changes?";
        public static string ConfigProfile_DiscardTitle => "Discard";
        public static string ConfigProfile_ErrEmpty => "Empty";
        public static string ConfigProfile_ErrInboundsMissing => "Inbounds missing";
        public static string ConfigProfile_ErrOutboundsNotAllowed => "Outbounds not allowed";
        public static string ConfigProfile_ErrRootMustBeObject => "Object required";
        public static string ConfigProfile_ErrTunInboundMissing => "TUN required";
        public static string ConfigProfile_ErrTunInboundNotAllowed => "TUN not allowed";
        public static string ConfigProfile_ExternalChangeWarning => "Unsaved edits";
        public static string ConfigProfile_LoadFailed => "Load failed";
        public static string ConfigProfile_PreviewNoServer => "No server";
        public static string ConfigProfile_PreviewStaleMsg => "Save first";
        public static string ConfigProfile_PreviewStaleTitle => "Unsaved";
        public static string ConfigProfile_PreviewTitle => "Preview";
        public static string ConfigProfile_ResetMsg => "Reset?";
        public static string ConfigProfile_ResetTitle => "Reset";
        public static string ConfigProfile_Saved => "Saved";
        public static string ConfigProfile_SavedInactive => "Saved inactive";
        public static string ConfigProfile_SaveFailedTitle => "Save failed";
        public static string ConfigProfile_SettingsUnwritable => "Unwritable";
        public static string ConfigProfile_WarnNoAutoSystemRouting => "No auto routes";
        public static string ConfigProfile_WarnNoSystemProxyInbound => "No system proxy";
    }
}
