using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Grably.Platform.Display;

namespace Grably.App.Capture.Overlay;

/// <summary>
/// A borderless, topmost window covering exactly one monitor (placed in physical pixels, so mixed-DPI setups line
/// up). Pointer positions are read with GetCursorPos in physical pixels rather than from WPF, which keeps a drag
/// that crosses monitors exact. All overlays share one <see cref="RegionSelection"/>.
/// </summary>
public sealed class CaptureOverlayWindow : Window
{
    private readonly RegionSelection _selection;
    private readonly OverlaySurface _surface;
    private bool _confirmOnRelease;

    public CaptureOverlayWindow(RegionSelection selection, MonitorInfo monitor, PixelBuffer desktopImage, PixelRect desktop)
    {
        _selection = selection;
        Monitor = monitor;
        _surface = new OverlaySurface(selection, monitor, desktopImage, desktop);

        Title = "Grably capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X;
        Top = monitor.Bounds.Y;
        Width = 100;
        Height = 100;
        Cursor = Cursors.Cross;
        Content = _surface;

        SourceInitialized += (_, _) => Place();
        Loaded += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        _selection.Changed += OnSelectionChanged;
        Closed += (_, _) => _selection.Changed -= OnSelectionChanged;
    }

    public MonitorInfo Monitor { get; }

    /// <summary>Takes keyboard focus (for Esc, Enter and arrow keys).</summary>
    public void FocusOverlay()
    {
        Activate();
        _surface.Focus();
        Keyboard.Focus(_surface);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _selection.MovePointer(ScreenPointer.Position());
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = ScreenPointer.Position();
        CaptureMouse();
        if (e.ClickCount == 2 && _selection.Phase == SelectionPhase.Adjusting && _selection.Selection is { } s && s.Contains(p))
        {
            // Confirm on the button up, so the up is not left to land on the app underneath the closed overlay.
            _confirmOnRelease = true;
            return;
        }

        _selection.Press(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        if (_confirmOnRelease)
        {
            _confirmOnRelease = false;
            _selection.Confirm();
            return;
        }

        _selection.Release(ScreenPointer.Position());
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        // Cancel on button up, and keep the up to ourselves: otherwise it lands on the app underneath once the
        // overlay is gone and opens that app's context menu.
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ReleaseMouseCapture();
        e.Handled = true;
        _selection.Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var resize = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                _selection.Cancel();
                break;
            case Key.Enter:
                _selection.Confirm();
                break;
            case Key.Left:
                _selection.Nudge(-step, 0, resize);
                break;
            case Key.Right:
                _selection.Nudge(step, 0, resize);
                break;
            case Key.Up:
                _selection.Nudge(0, -step, resize);
                break;
            case Key.Down:
                _selection.Nudge(0, step, resize);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Place() => ScreenPointer.PlaceTopmost(new WindowInteropHelper(this).Handle, Monitor.Bounds);

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_surface.NeedsRedraw())
        {
            _surface.InvalidateVisual();
        }

        Cursor = _selection.HitHandle(_selection.Pointer) switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Cursors.SizeNS,
            ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => Cursors.SizeNWSE,
            ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom => Cursors.SizeNESW,
            _ when _selection.Phase is SelectionPhase.Adjusting or SelectionPhase.Moving
                && _selection.Selection is { } s && s.Contains(_selection.Pointer) => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }
}
