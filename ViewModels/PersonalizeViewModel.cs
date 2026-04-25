using System;
using System.Threading.Tasks;
using Windows.UI;
using XrayUI.Helpers;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class PersonalizeViewModel : ObservableObject
    {
        private readonly SettingsService _settings;

        private Color _ssColor;
        private Color _vlessColor;
        private Color _vmessColor;
        private Color _hysteria2Color;
        private Color _fallbackColor;

        private int _selectedThemeIndex;
        private int _selectedBackdropIndex;

        public event EventHandler? CloseRequested;

        public PersonalizeViewModel(SettingsService settings)
        {
            _settings = settings;
        }

        // ── Colors ────────────────────────────────────────────────────────────

        public Color SsColor
        {
            get => _ssColor;
            set
            {
                if (SetProperty(ref _ssColor, value))
                {
                    ProtocolColorStore.Ss = value;
                    ProtocolColorStore.NotifyColorsChanged();
                }
            }
        }

        public Color VlessColor
        {
            get => _vlessColor;
            set
            {
                if (SetProperty(ref _vlessColor, value))
                {
                    ProtocolColorStore.Vless = value;
                    ProtocolColorStore.NotifyColorsChanged();
                }
            }
        }

        public Color VmessColor
        {
            get => _vmessColor;
            set
            {
                if (SetProperty(ref _vmessColor, value))
                {
                    ProtocolColorStore.Vmess = value;
                    ProtocolColorStore.NotifyColorsChanged();
                }
            }
        }

        public Color Hysteria2Color
        {
            get => _hysteria2Color;
            set
            {
                if (SetProperty(ref _hysteria2Color, value))
                {
                    ProtocolColorStore.Hysteria2 = value;
                    ProtocolColorStore.NotifyColorsChanged();
                }
            }
        }

        public Color FallbackColor
        {
            get => _fallbackColor;
            set
            {
                if (SetProperty(ref _fallbackColor, value))
                {
                    ProtocolColorStore.Fallback = value;
                    ProtocolColorStore.NotifyColorsChanged();
                }
            }
        }

        // ── Theme ─────────────────────────────────────────────────────────────
        // Bound TwoWay to CommunityToolkit Segmented.SelectedIndex.
        // 0 = Light, 1 = Dark, 2 = System/Default

        public int SelectedThemeIndex
        {
            get => _selectedThemeIndex;
            set
            {
                if (!SetProperty(ref _selectedThemeIndex, value)) return;
                var theme = value switch
                {
                    0 => ElementTheme.Light,
                    1 => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
                ThemeHelper.ApplyTheme(theme);
            }
        }

        // ── Backdrop ──────────────────────────────────────────────────────────

        public int SelectedBackdropIndex
        {
            get => _selectedBackdropIndex;
            set
            {
                if (!SetProperty(ref _selectedBackdropIndex, value)) return;
                ThemeHelper.ApplyBackdrop(value == 1 ? "Acrylic" : "Mica");
            }
        }

        // ── Commands ──────────────────────────────────────────────────────────

        [RelayCommand]
        private void ResetColors()
        {
            SsColor        = Color.FromArgb(255,  96, 165, 250);
            VlessColor     = Color.FromArgb(255,  52, 211, 153);
            VmessColor     = Color.FromArgb(255, 167, 139, 250);
            Hysteria2Color = Color.FromArgb(255, 251, 146,  60);
            FallbackColor  = Color.FromArgb(255, 148, 163, 184);
        }

        [RelayCommand]
        private async Task Done()
        {
            var s = await _settings.LoadSettingsAsync();
            ProtocolColorStore.SaveTo(s);
            s.ThemeSetting = ThemeHelper.CurrentTheme switch
            {
                ElementTheme.Light   => "Light",
                ElementTheme.Dark    => "Dark",
                _                    => "Default"
            };
            s.BackdropSetting = ThemeHelper.CurrentBackdrop;
            await _settings.SaveSettingsAsync(s);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── Initialization ────────────────────────────────────────────────────

        public void LoadFromStore()
        {
            _ssColor        = ProtocolColorStore.Ss;
            _vlessColor     = ProtocolColorStore.Vless;
            _vmessColor     = ProtocolColorStore.Vmess;
            _hysteria2Color = ProtocolColorStore.Hysteria2;
            _fallbackColor  = ProtocolColorStore.Fallback;

            OnPropertyChanged(nameof(SsColor));
            OnPropertyChanged(nameof(VlessColor));
            OnPropertyChanged(nameof(VmessColor));
            OnPropertyChanged(nameof(Hysteria2Color));
            OnPropertyChanged(nameof(FallbackColor));

            _selectedThemeIndex = ThemeHelper.CurrentTheme switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark  => 1,
                _                  => 2,
            };
            OnPropertyChanged(nameof(SelectedThemeIndex));

            _selectedBackdropIndex = ThemeHelper.CurrentBackdrop == "Acrylic" ? 1 : 0;
            OnPropertyChanged(nameof(SelectedBackdropIndex));
        }
    }
}
