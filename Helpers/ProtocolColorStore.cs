using System;
using System.Globalization;
using Windows.UI;
using XrayUI.Models;

namespace XrayUI.Helpers
{
    /// <summary>
    /// Runtime mutable store for protocol badge colors.
    /// Loaded from AppSettings at startup; updated in real-time by the Personalize flyout.
    /// </summary>
    public static class ProtocolColorStore
    {
        // Tailwind 400 defaults — readable on both light & dark surfaces
        public static Color Ss        { get; set; } = Color.FromArgb(255,  96, 165, 250);
        public static Color Vless     { get; set; } = Color.FromArgb(255,  52, 211, 153);
        public static Color Vmess     { get; set; } = Color.FromArgb(255, 167, 139, 250);
        public static Color Hysteria2 { get; set; } = Color.FromArgb(255, 251, 146,  60);
        public static Color Fallback  { get; set; } = Color.FromArgb(255, 148, 163, 184);

        public static event EventHandler? ColorsChanged;

        public static Color GetColor(string protocol) => protocol.ToLowerInvariant() switch
        {
            "ss" or "shadowsocks" => Ss,
            "vless"               => Vless,
            "vmess"               => Vmess,
            "hysteria2"           => Hysteria2,
            _                     => Fallback
        };

        public static void NotifyColorsChanged() => ColorsChanged?.Invoke(null, EventArgs.Empty);

        public static void LoadFrom(AppSettings s)
        {
            if (s.ColorSs        is not null) Ss        = ParseHex(s.ColorSs,        Ss);
            if (s.ColorVless     is not null) Vless     = ParseHex(s.ColorVless,     Vless);
            if (s.ColorVmess     is not null) Vmess     = ParseHex(s.ColorVmess,     Vmess);
            if (s.ColorHysteria2 is not null) Hysteria2 = ParseHex(s.ColorHysteria2, Hysteria2);
            if (s.ColorFallback  is not null) Fallback  = ParseHex(s.ColorFallback,  Fallback);
        }

        public static void SaveTo(AppSettings s)
        {
            s.ColorSs        = ToHex(Ss);
            s.ColorVless     = ToHex(Vless);
            s.ColorVmess     = ToHex(Vmess);
            s.ColorHysteria2 = ToHex(Hysteria2);
            s.ColorFallback  = ToHex(Fallback);
        }

        private static Color ParseHex(string hex, Color fallback)
        {
            if (hex.StartsWith('#')) hex = hex[1..];
            if (hex.Length == 6 &&
                byte.TryParse(hex[0..2], NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex[2..4], NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex[4..6], NumberStyles.HexNumber, null, out var b))
            {
                return Color.FromArgb(255, r, g, b);
            }
            return fallback;
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
