// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Hush.App.ViewModels;

namespace Hush.App.Views;

/// <summary>
/// Floating overlay — renders a smooth connected-line waveform on a Canvas,
/// driven by live audio level. Clean pill shape, white frosted glass.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // ── Waveform state ───────────────────────────────────────────────────────
    private const int PointCount = 48;
    private const double WavePadX = 24;
    private const double WavePadY = 6;
    private const double IdleAmplitude = 1.0;

    // Three distinct waveform lines — each reacts to audio differently.
    private record WaveConfig(
        Color Color, double Thickness, double Opacity,
        double Freq1, double Speed1, double Weight1,
        double Freq2, double Speed2, double Weight2,
        double Freq3, double Speed3, double Weight3,
        double ResponseAttack, double ResponseDecay, double PhaseOffset);

    private static readonly WaveConfig[] WaveConfigs =
    [
        // Line 1: Purple — primary, clean sinusoidal, one full cycle (peak + trough)
        new(Color.FromRgb(147, 51, 234), 2.0, 1.0,
            Freq1: 5.0, Speed1: 3.2, Weight1: 0.90,
            Freq2: 10.0, Speed2: -1.5, Weight2: 0.07,
            Freq3: 15.0, Speed3: 2.0, Weight3: 0.03,
            ResponseAttack: 0.65, ResponseDecay: 0.15, PhaseOffset: 0.0),

        // Line 2: Soft coral — secondary, higher frequency, energetic
        new(Color.FromRgb(251, 113, 133), 1.2, 0.45,
            Freq1: 7.5, Speed1: 5.2, Weight1: 0.40,
            Freq2: 14.0, Speed2: -3.8, Weight2: 0.35,
            Freq3: 20.0, Speed3: 7.5, Weight3: 0.25,
            ResponseAttack: 0.60, ResponseDecay: 0.12, PhaseOffset: 1.0),

        // Line 3: Light indigo — tertiary, low frequency, trailing echo
        new(Color.FromRgb(129, 140, 248), 1.0, 0.35,
            Freq1: 4.0, Speed1: 2.8, Weight1: 0.55,
            Freq2: 7.0, Speed2: -2.0, Weight2: 0.28,
            Freq3: 12.0, Speed3: 4.5, Weight3: 0.17,
            ResponseAttack: 0.45, ResponseDecay: 0.08, PhaseOffset: 2.2),
    ];

    private Canvas? _canvas;
    private Polyline[]? _waveLines;
    private readonly double[] _displayLevels = new double[3];
    private DispatcherTimer? _animTimer;
    private float _targetLevel;
    private double _animTime;
    private OverlayViewModel? _viewModel;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _canvas = this.FindControl<Canvas>("WaveCanvas");
        if (_canvas is not null)
        {
            _waveLines = new Polyline[WaveConfigs.Length];
            for (int i = 0; i < WaveConfigs.Length; i++)
            {
                var cfg = WaveConfigs[i];
                _waveLines[i] = new Polyline
                {
                    Stroke = new SolidColorBrush(cfg.Color),
                    StrokeThickness = cfg.Thickness,
                    StrokeLineCap = PenLineCap.Round,
                    Opacity = 0,
                };
                _canvas.Children.Add(_waveLines[i]);
            }
        }

        if (DataContext is OverlayViewModel vm)
        {
            _viewModel = vm;
            ApplyOverlayPlacement(vm);
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAnimTick);
        _animTimer.Start();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        _animTimer?.Stop();
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnUnloaded(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;

        if (e.PropertyName == nameof(OverlayViewModel.AudioLevel))
        {
            _targetLevel = (float)Math.Clamp(Math.Pow(Math.Max(_viewModel.AudioLevel, 0f), 0.50), 0.0, 1.0);
            return;
        }

        if (e.PropertyName is nameof(OverlayViewModel.OverlayOpacity) or nameof(OverlayViewModel.OverlayPosition))
            ApplyOverlayPlacement(_viewModel);
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_waveLines is null || _canvas is null) return;

        const double dt = 0.016;
        _animTime += dt;

        bool isListening = _viewModel?.IsListening ?? false;
        double target = isListening ? _targetLevel : 0.0;

        double w = _canvas.Bounds.Width;
        double h = _canvas.Bounds.Height;
        if (w < 1 || h < 1) return;

        double midY = h / 2.0;
        double drawWidth = w - WavePadX * 2;
        double maxAmp = (h / 2.0) - WavePadY;

        for (int li = 0; li < WaveConfigs.Length; li++)
        {
            var cfg = WaveConfigs[li];

            // Each line has its own smoothed audio level (different attack/decay)
            double response = target > _displayLevels[li] ? cfg.ResponseAttack : cfg.ResponseDecay;
            _displayLevels[li] += (target - _displayLevels[li]) * response;
            double level = _displayLevels[li];

            var points = new Points();

            for (int i = 0; i < PointCount; i++)
            {
                double t = i / (double)(PointCount - 1);
                double x = WavePadX + t * drawWidth;

                // Flattened bell envelope — stays near full amplitude from ~25% to ~75%,
                // tapers at edges. This pushes peaks/troughs outward from center.
                double envelope = Math.Pow(Math.Sin(Math.PI * t), 0.7);

                // Composite wave — use tanh to soft-clip peaks for a rounder shape
                // instead of raw sine stacking which looks too mechanical.
                double raw =
                    Math.Sin(t * cfg.Freq1 + _animTime * cfg.Speed1 + cfg.PhaseOffset) * cfg.Weight1 +
                    Math.Sin(t * cfg.Freq2 + _animTime * cfg.Speed2 + cfg.PhaseOffset * 0.7) * cfg.Weight2 +
                    Math.Sin(t * cfg.Freq3 + _animTime * cfg.Speed3 + cfg.PhaseOffset * 1.3) * cfg.Weight3;

                // Soft-clip for more natural peaks (less spiky, more rounded)
                double wave = Math.Tanh(raw * 1.4);

                double idle = IdleAmplitude * Math.Sin(t * 3.0 + _animTime * 1.2 + cfg.PhaseOffset) * envelope;
                double amp = idle + wave * envelope * maxAmp * level;

                points.Add(new Point(x, midY + amp));
            }

            _waveLines[li].Points = points;

            // Opacity: base from config, scaled by audio level
            double opacity = isListening
                ? cfg.Opacity * Math.Max(0.5, level)
                : Math.Max(0, _waveLines[li].Opacity - 0.04);
            _waveLines[li].Opacity = opacity;
        }
    }

    private void ApplyOverlayPlacement(OverlayViewModel vm)
    {
        Opacity = Math.Clamp(vm.OverlayOpacity, 0.35, 1.0);

        var screen = Screens.Primary;
        if (screen is null) return;

        var workArea = screen.WorkingArea;
        var margin = 36;

        var x = vm.OverlayPosition switch
        {
            "bottom-left" or "top-left" => workArea.X + margin,
            "bottom-right" or "top-right" => workArea.X + workArea.Width - (int)Width - margin,
            _ => workArea.X + (workArea.Width - (int)Width) / 2
        };

        var y = vm.OverlayPosition switch
        {
            "top-left" or "top-right" => workArea.Y + margin,
            _ => workArea.Y + workArea.Height - (int)Height - margin
        };

        Position = new PixelPoint(x, y);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } handle)
        {
            var hwnd = handle.Handle;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        }
    }
}

