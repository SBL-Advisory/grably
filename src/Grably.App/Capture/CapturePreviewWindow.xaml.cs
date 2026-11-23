using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grably.Core.Geometry;
using Grably.Platform.Display;

namespace Grably.App.Capture;

/// <summary>
/// The "show preview" after-capture option: a small card in the corner of the work area with the screenshot and
/// quick actions. It disappears by itself after a few seconds unless the pointer is over it.
/// </summary>
public partial class CapturePreviewWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);

    private readonly string _path;
    private readonly BitmapSource _image;
    private readonly PixelRect _workArea;
    private readonly Action _edit;
    private readonly DispatcherTimer _timer = new() { Interval = Lifetime };
    private Point? _pressedAt;
    private double _placedAtScale;

    /// <param name="workArea">Work area (physical pixels) of the monitor to show it on.</param>
    /// <param name="edit">Opens the screenshot in the editor.</param>
    public CapturePreviewWindow(string path, BitmapSource image, PixelRect workArea, Action edit)
    {
        _path = path;
        _image = image;
        _workArea = workArea;
        _edit = edit;
        InitializeComponent();
        PreviewImage.Source = image;
        TitleText.Text = Path.GetFileName(path);

        _timer.Tick += (_, _) =>
        {
            if (!IsMouseOver)
            {
                Close();
            }
        };
        SourceInitialized += (_, _) => WindowActions.ExcludeFromCapture(new WindowInteropHelper(this).Handle); // not in recordings
        Loaded += (_, _) =>
        {
            PlaceInCorner();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();

        // It first appears with the DPI of wherever Windows put it; on a monitor with another scale, size it again.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (VisualTreeHelper.GetDpi(this).DpiScaleX != _placedAtScale)
            {
                PlaceInCorner();
            }
        });
    }

    private void PlaceInCorner()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _placedAtScale = scale;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var margin = (int)(8 * scale);
        ScreenPointer.PlaceTopmost(new WindowInteropHelper(this).Handle,
            new PixelRect(_workArea.Right - width - margin, _workArea.Bottom - height - margin, width, height));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _edit();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ImageOutput.CopyToClipboard(_image);
            Close();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            TitleText.Text = "The clipboard is busy";
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Image_MouseDown(object sender, MouseButtonEventArgs e) => _pressedAt = e.GetPosition(this);

    private void Image_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedAt is not null)
        {
            _pressedAt = null;
            Edit_Click(sender, e);
        }
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedAt = null; // a drag, not a click
        var data = new DataObject();
        data.SetFileDropList([_path]);
        DragDrop.DoDragDrop(PreviewImage, data, DragDropEffects.Copy);
    }
}
