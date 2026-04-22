using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.ViewModels
{
    public partial class CustomRulesViewModel : ObservableObject
    {
        private readonly SettingsService _settings;
        private readonly XrayService _xray;
        private readonly Func<Task>? _reapplyRouting;

        private bool _isEffectiveNow;

        public ObservableCollection<CustomRoutingRule> Rules { get; } = new();

        /// <summary>True iff current RoutingMode is "smart". UI shows a banner when false.</summary>
        public bool IsEffectiveNow
        {
            get => _isEffectiveNow;
            private set
            {
                if (SetProperty(ref _isEffectiveNow, value))
                {
                    OnPropertyChanged(nameof(IsNotEffectiveNow));
                    OnPropertyChanged(nameof(NotEffectiveVisibility));
                }
            }
        }

        public bool IsNotEffectiveNow => !_isEffectiveNow;

        // Direct Visibility binding — avoids converter lookup in Window root.
        public Visibility NotEffectiveVisibility => _isEffectiveNow ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>
        /// View is expected to open AddRuleDialog when this fires.
        /// Payload == null → Add new; Payload != null → Edit existing.
        /// After dialog confirms, View calls back into <see cref="AddNewRule"/>
        /// or <see cref="ReplaceRule"/>.
        /// </summary>
        public event EventHandler<CustomRoutingRule?>? ShowAddOrEditDialogRequested;

        /// <summary>View closes the window when this fires.</summary>
        public event EventHandler? CloseRequested;

        public CustomRulesViewModel(SettingsService settings, XrayService xray, Func<Task>? reapplyRouting)
        {
            _settings       = settings;
            _xray           = xray;
            _reapplyRouting = reapplyRouting;
        }

        public async Task LoadAsync()
        {
            var s = await _settings.LoadSettingsAsync();

            Rules.Clear();
            if (s.CustomRules != null)
            {
                foreach (var r in s.CustomRules)
                    Rules.Add(r.Clone());   // deep copy so UI edits don't mutate persisted list
            }

            IsEffectiveNow = s.RoutingMode == "smart";
        }

        // ── Called by View after dialog returns ───────────────────────────────
        public void AddNewRule(CustomRoutingRule rule) => Rules.Add(rule);

        public void ReplaceRule(CustomRoutingRule original, CustomRoutingRule updated)
        {
            var idx = Rules.IndexOf(original);
            if (idx >= 0) Rules[idx] = updated;
        }

        // ── Commands ──────────────────────────────────────────────────────────

        [RelayCommand]
        private void AddRule() => ShowAddOrEditDialogRequested?.Invoke(this, null);

        [RelayCommand]
        private void EditRule(CustomRoutingRule rule) =>
            ShowAddOrEditDialogRequested?.Invoke(this, rule);

        [RelayCommand]
        private void DeleteRule(CustomRoutingRule rule) => Rules.Remove(rule);

        [RelayCommand]
        private async Task Save()
        {
            var s = await _settings.LoadSettingsAsync();
            s.CustomRules = Rules.Count == 0
                ? null
                : Rules.Select(r => r.Clone()).ToList();

            try
            {
                await _settings.SaveSettingsAsync(s);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CustomRules] Failed to persist: {ex.Message}");
            }

            // Rebuild xray config + restart when running in smart mode.
            if (_reapplyRouting != null && _xray.IsRunning && s.RoutingMode == "smart")
            {
                try
                {
                    await _reapplyRouting();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CustomRules] Failed to reapply routing: {ex.Message}");
                }
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
