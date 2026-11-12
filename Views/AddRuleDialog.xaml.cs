using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using XrayUI.Helpers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Views
{
    public sealed partial class AddRuleDialog
    {
        public CustomRoutingRule? Result { get; private set; }

        private readonly WindowId _hostWindowId;
        private bool _isPicking;

        public AddRuleDialog(WindowId hostWindowId, CustomRoutingRule? existing = null)
        {
            _hostWindowId = hostWindowId;
            this.InitializeComponent();
            this.RequestedTheme = ThemeHelper.ActualTheme;

            Title             = L.AddRule_Title;
            PrimaryButtonText = L.Dialog_Add;
            CloseButtonText   = L.Dialog_Cancel;

            // Wire up event handlers in code-behind (NOT via XAML markup).
            // The XAML-compiler-generated Connect path for SelectionChanged on a
            // ContentDialog can fail to fire under AOT in WinUI 3; explicit
            // subscription here is the AOT-safe pattern (cf. DialogService.cs).
            TypeComboBox.SelectionChanged         += TypeComboBox_SelectionChanged;
            BrowseFormatComboBox.SelectionChanged += BrowseFormatComboBox_SelectionChanged;
            BrowseButton.Click                    += BrowseButton_Click;

            if (existing != null)
            {
                Title             = L.AddRule_EditTitle;
                PrimaryButtonText = L.Dialog_Save;

                TypeComboBox.SelectedIndex = existing.Type switch
                {
                    "ip"      => 1,
                    "process" => 2,
                    _         => 0,
                };
                MatchTextBox.Text              = existing.Match;
                OutboundComboBox.SelectedIndex = existing.OutboundTag switch
                {
                    "direct" => 1,
                    "block"  => 2,
                    _        => 0,   // proxy
                };
            }

            // Sync BrowsePanel + placeholder + hint for the initial Type selection
            // (SelectionChanged may not fire for SelectedIndex set above pre-load).
            ApplyTypeUiState();
            ApplyBrowseFormatUiState();

            this.PrimaryButtonClick += OnPrimaryClick;
            this.Closing += (_, args) => args.Cancel = _isPicking;
        }

        // ── Type changes: toggle BrowsePanel, swap placeholder + hint ─────────

        private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ApplyTypeUiState();

        private void ApplyTypeUiState()
        {
            var tag = GetSelectedType();

            if (BrowsePanel is not null)
                BrowsePanel.Visibility = tag == "process" ? Visibility.Visible : Visibility.Collapsed;

            if (MatchTextBox is not null)
            {
                MatchTextBox.PlaceholderText = tag switch
                {
                    "ip"      => L.AddRule_PlaceholderIp,
                    "process" => L.AddRule_PlaceholderProcess,
                    _         => L.AddRule_PlaceholderDomain,
                };
            }

            if (HintTextBlock is not null)
            {
                HintTextBlock.Text = tag switch
                {
                    "ip"      => L.AddRule_HintIp,
                    "process" => L.AddRule_HintProcess,
                    _         => L.AddRule_HintDomain,
                };
            }
        }

        // ── Browse format changes: swap button label between exe / folder ────

        private void BrowseFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => ApplyBrowseFormatUiState();

        private void ApplyBrowseFormatUiState()
        {
            if (BrowseButtonText is null) return;
            var isFolder = GetSelectedBrowseFormat() == "folder";
            BrowseButtonText.Text = isFolder ? L.AddRule_BrowseFolder : L.AddRule_BrowseExe;
            if (BrowseButtonIcon is not null)
            {
                BrowseButtonIcon.Glyph = isFolder ? "\uE8DA" : "\uE8E5";
            }
        }

        // Native AOT can be brittle around object-valued ComboBoxItem.Tag from XAML.
        // These lists are static, so index mapping keeps dialog state deterministic.
        private string GetSelectedType() => TypeComboBox.SelectedIndex switch
        {
            1 => "ip",
            2 => "process",
            _ => "domain",
        };

        private string GetSelectedBrowseFormat() => BrowseFormatComboBox.SelectedIndex switch
        {
            1 => "path",
            2 => "folder",
            _ => "name",
        };

        private string GetSelectedOutboundTag() => OutboundComboBox.SelectedIndex switch
        {
            1 => "direct",
            2 => "block",
            _ => "proxy",
        };

        // ── Browse click: file picker for name/path, folder picker for folder ──

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPicking) return;
            var format = GetSelectedBrowseFormat();
            _isPicking = true;
            BrowseButton.IsEnabled = false;
            BrowseFormatComboBox.IsEnabled = false;
            TypeComboBox.IsEnabled = false;
            IsPrimaryButtonEnabled = false;
            BrowseButtonText.Text = L.AddRule_PickerWaiting;
            ErrorText.Visibility = Visibility.Collapsed;
            var stopwatch = Stopwatch.StartNew();
            Debug.WriteLine($"[AddRuleDialog] Picker requested: {format}");

            try
            {
                // Desktop pickers return paths directly and bind to this dialog's
                // actual host window. They also support elevated TUN sessions.
                if (format == "folder")
                {
                    var folderPicker = new FolderPicker(_hostWindowId);
                    var folder = await folderPicker.PickSingleFolderAsync();
                    if (folder is null) return;

                    // Trailing backslash matches all executables in the directory.
                    AppendMatchValues((string[])[folder.Path.TrimEnd('\\') + "\\"]);
                    return;
                }

                var picker = new FileOpenPicker(_hostWindowId);
                picker.FileTypeFilter.Add(".exe");
                var files = await picker.PickMultipleFilesAsync();
                if (files.Count == 0) return;

                AppendMatchValues(files.Select(file => format == "path" ? file.Path : Path.GetFileName(file.Path)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AddRuleDialog] Picker failed ({format}): {ex}");
                ErrorText.Text = Loc.Format("AddRule_PickerFailed", $"0x{ex.HResult:X8}");
                ErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                // Includes time spent choosing/cancelling; not a startup latency measurement.
                Debug.WriteLine($"[AddRuleDialog] Picker operation ended: {format}, {stopwatch.ElapsedMilliseconds} ms");
                _isPicking = false;
                BrowseButton.IsEnabled = true;
                BrowseFormatComboBox.IsEnabled = true;
                TypeComboBox.IsEnabled = true;
                IsPrimaryButtonEnabled = true;
                ApplyBrowseFormatUiState();
            }
        }

        private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var match = MatchTextBox.Text?.Trim() ?? "";
            if (CustomRuleValueParser.Parse(match).Count == 0)
            {
                ErrorText.Text = L.AddRule_ErrorEmpty;
                ErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var typeTag     = GetSelectedType();
            var outboundTag = GetSelectedOutboundTag();

            Result = new CustomRoutingRule
            {
                Type        = typeTag,
                Match       = match,
                OutboundTag = outboundTag,
                IsEnabled   = true,
            };
        }

        private void AppendMatchValues(IEnumerable<string> additions)
        {
            var values = CustomRuleValueParser.Parse(MatchTextBox.Text);
            foreach (var addition in additions)
            {
                if (!values.Contains(addition, StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(addition);
                }
            }

            MatchTextBox.Text = string.Join(Environment.NewLine, values);
        }
    }
}
