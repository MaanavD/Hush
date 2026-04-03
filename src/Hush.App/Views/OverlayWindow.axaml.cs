using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Hush.App.ViewModels;

namespace Hush.App.Views;

/// <summary>
/// Floating overlay window shown while dictation is active.
/// Must never steal keyboard focus from the target application.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    // WS_EX_NOACTIVATE  — window never becomes the active window on click or show.
    // WS_EX_TRANSPARENT — mouse events pass through to windows beneath.
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // The centered meter expands around a visible midpoint line.
    // Audio drives the overall amplitude; light phase offsets keep adjacent bars
    // from moving in lock-step without obscuring the live input signal.

    private static readonly double[] BarPhases = [0.0, 1.1, 2.3, 0.7, 1.8];
    private static readonly double[] BarFreqs = [1.5, 2.2, 1.2, 2.6, 1.0];
    private static readonly double[] BarWeights = [0.82, 1.0, 1.18, 0.98, 0.84];
    private const double MaxBarHeight = 26.0;
    private const double MinBarHeight = 2.0;

    private Rectangle[]?    _bars;
    private DispatcherTimer? _animTimer;
    private float            _targetLevel;
    private double           _displayLevel;
    private double           _animTime;

    public OverlayWindow()
    {
        InitializeComponent();
        PositionBottomCenter();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _bars =
        [
            this.FindControl<Rectangle>("Bar0")!,
            this.FindControl<Rectangle>("Bar1")!,
            this.FindControl<Rectangle>("Bar2")!,
            this.FindControl<Rectangle>("Bar3")!,
            this.FindControl<Rectangle>("Bar4")!,
        ];

        // Capture the latest audio target; per-frame smoothing happens in OnAnimTick.
        if (DataContext is OverlayViewModel vm)
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OverlayViewModel.AudioLevel) &&
                    DataContext is OverlayViewModel v)
                    _targetLevel = (float)Math.Clamp(Math.Pow(Math.Max(v.AudioLevel, 0f), 0.65), 0.0, 1.0);
            };

        // 30 fps animation timer provides fast attack and controlled decay.
        _animTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnAnimTick);
        _animTimer.Start();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        _animTimer?.Stop();
        base.OnUnloaded(e);
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_bars is null) return;

        _animTime += 0.033; // seconds per tick

        bool isListening = (DataContext as OverlayViewModel)?.IsListening ?? false;
        double target = isListening ? _targetLevel : 0.0;
        double response = target > _displayLevel ? 0.62 : 0.24;
        _displayLevel += (target - _displayLevel) * response;

        for (int i = 0; i < _bars.Length; i++)
        {
            double ripple = 0.86 + 0.14 * Math.Abs(Math.Sin(BarFreqs[i] * _animTime + BarPhases[i]));
            double spread = 0.05 * Math.Sin(_animTime * 4.8 + i * 0.9);
            double barLevel = Math.Clamp((_displayLevel + spread) * BarWeights[i] * ripple, 0.0, 1.0);
            double h = MinBarHeight + barLevel * (MaxBarHeight - MinBarHeight);
            _bars[i].Height = h;
        }
    }

    private void PositionBottomCenter()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var workArea = screen.WorkingArea;
        var x = workArea.X + (workArea.Width - (int)Width) / 2;
        var y = workArea.Y + workArea.Height - (int)Height - 36;
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

