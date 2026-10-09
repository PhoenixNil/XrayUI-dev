using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using XrayUI.Helpers;

namespace XrayUI.Views
{
    public sealed partial class TrayPanelControl
    {
        // The connection glyph on its 24-unit grid, one point for each corner of the square.
        // The triangle's tip counts twice, so the morph opens it into the square's right edge
        // and the shape stays symmetric top to bottom the whole way. The square sits inset by half
        // its stroke: the round-joined stroke draws the outer edge at 1..23 (13.75 DIP once the
        // Viewbox scales it) and makes the corner radius half its thickness, 4 units (2.5 DIP).
        private static readonly Point[] PlayGlyph = [new(6, 4.5), new(20, 12), new(20, 12), new(6, 19.5)];
        private static readonly Point[] StopGlyph = [new(5, 5), new(19, 5), new(19, 19), new(5, 19)];
        private static readonly TimeSpan GlyphMorphDuration = TimeSpan.FromMilliseconds(300);
        private const double PlayStrokeThickness = 1.4;
        private const double StopStrokeThickness = 8;

        private ImageSource? _idleIcon;
        private ImageSource? _runningIcon;
        private readonly UISettings _uiSettings = new();
        private bool _isLoaded;
        private bool _glyphShowsConnected;
        private Storyboard? _glyphMorph;
        private long _enabledToken;

        public TrayPanelViewModel ViewModel { get; }

        public TrayPanelControl(TrayPanelViewModel viewModel)
        {
            // Assigned before InitializeComponent so the TwoWay bindings on the switches start
            // from the VM's values instead of pushing control defaults into it.
            ViewModel = viewModel;
            InitializeComponent();

            ConnectionGlyph.Stroke = LocalBrush("ConnectionStroke");
            ConnectionGlyph.Fill = LocalBrush("ConnectionFill");
            UpdateConnectionAppearance();
            SnapConnectionGlyph();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SetLabel(OpenMainButton, L.TrayPanel_OpenMain);
            SetLabel(ExitButton, L.Tray_Exit);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded) return;
            _isLoaded = true;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ActualThemeChanged += OnActualThemeChanged;
            _enabledToken = ConnectionButton.RegisterPropertyChangedCallback(
                IsEnabledProperty, (_, _) => UpdateGlyphOpacity());
            UpdateConnectionAppearance();
            SnapConnectionGlyph();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            _isLoaded = false;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ActualThemeChanged -= OnActualThemeChanged;
            ConnectionButton.UnregisterPropertyChangedCallback(IsEnabledProperty, _enabledToken);
            _glyphMorph?.Stop();
        }

        private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateConnectionAppearance();

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TrayPanelViewModel.IsConnected)) return;
            UpdateConnectionAppearance();
            MorphConnectionGlyph();
        }

        private void UpdateConnectionAppearance()
        {
            var running = ViewModel.IsConnected;
            LocalBrush("ConnectionStroke").Color = StrokeColor(running);
            LocalBrush("ConnectionFill").Color = FillColor(running);
            ConnectionGlyph.StrokeThickness = running ? StopStrokeThickness : PlayStrokeThickness;
            SetLabel(ConnectionButton, running ? L.ControlPanel_Stop : L.ControlPanel_Start);
            UpdateGlyphOpacity();
        }

        private SolidColorBrush LocalBrush(string key) => (SolidColorBrush)ConnectionButton.Resources[key];

        // Read from the button's own resources, so it follows the panel's actual theme.
        private Color StopColor => LocalBrush("StopForeground").Color;

        private Color StrokeColor(bool running) => running ? StopColor : LocalBrush("IdleForeground").Color;

        // Idle, the fill is the stop red at zero alpha, so fading it in never passes through black.
        private Color FillColor(bool running) => running ? StopColor : StopColor with { A = 0 };

        private void UpdateGlyphOpacity() => ConnectionGlyph.Opacity = ConnectionButton.IsEnabled ? 1 : 0.4;

        private void SnapConnectionGlyph()
        {
            _glyphMorph?.Stop();
            _glyphShowsConnected = ViewModel.IsConnected;
            SetGlyphPoints(_glyphShowsConnected ? StopGlyph : PlayGlyph);
        }

        private void MorphConnectionGlyph()
        {
            var connected = ViewModel.IsConnected;
            if (connected == _glyphShowsConnected) return;

            // The new shape is set as the points' own values first. The storyboard only covers
            // the way there, and FillBehavior.Stop hands the points back to those values.
            var from = connected ? PlayGlyph : StopGlyph;
            SnapConnectionGlyph();
            if (!_uiSettings.AnimationsEnabled) return;

            // Plays on confirmed state, including changes made outside this panel. Both
            // directions ease out over the same 300 ms, so stopping is as quick as starting.
            var to = connected ? StopGlyph : PlayGlyph;
            DependencyObject[] targets = [GlyphFigure, GlyphSegment1, GlyphSegment2, GlyphSegment3];
            _glyphMorph = new Storyboard();
            for (var i = 0; i < to.Length; i++)
            {
                var point = new PointAnimationUsingKeyFrames
                {
                    EnableDependentAnimation = true,
                    FillBehavior = FillBehavior.Stop,
                };
                point.KeyFrames.Add(new DiscretePointKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                    Value = from[i],
                });
                point.KeyFrames.Add(new SplinePointKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(GlyphMorphDuration),
                    Value = to[i],
                    KeySpline = new KeySpline { ControlPoint1 = new(0.2, 0.75), ControlPoint2 = new(0.34, 0.94) },
                });
                Storyboard.SetTarget(point, targets[i]);
                Storyboard.SetTargetProperty(point, i == 0 ? "StartPoint" : "Point");
                _glyphMorph.Children.Add(point);
            }
            AddMorph(new ColorAnimation { From = StrokeColor(!connected), To = StrokeColor(connected) },
                LocalBrush("ConnectionStroke"), "Color");
            AddMorph(new ColorAnimation { From = FillColor(!connected), To = FillColor(connected) },
                LocalBrush("ConnectionFill"), "Color");
            AddMorph(new DoubleAnimation
            {
                From = connected ? PlayStrokeThickness : StopStrokeThickness,
                To = connected ? StopStrokeThickness : PlayStrokeThickness,
            }, ConnectionGlyph, "StrokeThickness");
            _glyphMorph.Begin();
        }

        // Same timing as the points, ending on the values UpdateConnectionAppearance already set.
        private void AddMorph(Timeline animation, DependencyObject target, string property)
        {
            animation.Duration = GlyphMorphDuration;
            animation.FillBehavior = FillBehavior.Stop;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            switch (animation)
            {
                case ColorAnimation color: color.EnableDependentAnimation = true; color.EasingFunction = ease; break;
                case DoubleAnimation number: number.EnableDependentAnimation = true; number.EasingFunction = ease; break;
            }
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            _glyphMorph!.Children.Add(animation);
        }

        // Point 0 starts the figure; the others end its three line segments. Reached through
        // the x:Name fields, not Segments[i]: under AOT that indexer hands back a plain
        // PathSegment wrapper, and a C# cast to LineSegment throws InvalidCastException.
        private void SetGlyphPoints(Point[] points)
        {
            GlyphFigure.StartPoint = points[0];
            GlyphSegment1.Point = points[1];
            GlyphSegment2.Point = points[2];
            GlyphSegment3.Point = points[3];
        }

        /// <summary>The tray icon pictures, decoded by the host window once it knows the DPI.</summary>
        public void SetAppIcons(ImageSource? idle, ImageSource? running)
        {
            _idleIcon = idle;
            _runningIcon = running;
            Bindings.Update();
        }

        public ImageSource? AppIconFor(bool running) => running ? _runningIcon : _idleIcon;

        /// <summary>Moves keyboard focus into the panel, so Esc and Tab work as soon as it opens.
        /// As pointer focus, because a click opened the panel: no focus rectangle until Tab.</summary>
        public void FocusFirstControl()
        {
            if (!ConnectionButton.Focus(FocusState.Pointer))
                OpenMainButton.Focus(FocusState.Pointer);
        }

        private static void SetLabel(Button button, string text)
        {
            ToolTipService.SetToolTip(button, text);
            AutomationProperties.SetName(button, text);
        }
    }
}
