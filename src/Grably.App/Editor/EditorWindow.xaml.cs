using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Grably.App.Shell;
using Grably.Core.Annotations;
using Grably.Core.Settings;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TextBox = System.Windows.Controls.TextBox;

namespace Grably.App.Editor;

public partial class EditorWindow : FluentWindow, IUnsavedWork
{
    private const string ItemClipboardFormat = "Grably.Annotation";

    /// <summary>Border plus the text view's own margin inside a plain WPF TextBox.</summary>
    private static readonly Thickness TextBoxInset = new(3, 1, 3, 1);

    private static readonly (EditorTool Tool, SymbolRegular Icon, string Name, Key Key)[] Tools =
    [
        (EditorTool.Select, SymbolRegular.Cursor24, "Select", Key.V),
        (EditorTool.Arrow, SymbolRegular.ArrowUpRight24, "Arrow", Key.A),
        (EditorTool.Line, SymbolRegular.Line24, "Line", Key.L),
        (EditorTool.Rectangle, SymbolRegular.RectangleLandscape24, "Rectangle", Key.R),
        (EditorTool.Ellipse, SymbolRegular.Circle24, "Ellipse", Key.E),
        (EditorTool.Pen, SymbolRegular.Pen24, "Pen", Key.P),
        (EditorTool.Highlighter, SymbolRegular.Highlight24, "Highlighter", Key.H),
        (EditorTool.Text, SymbolRegular.TextT24, "Text", Key.T),
        (EditorTool.Step, SymbolRegular.NumberCircle124, "Step number", Key.N),
        (EditorTool.Blur, SymbolRegular.Blur24, "Blur", Key.B),
        (EditorTool.Pixelate, SymbolRegular.Grid24, "Pixelate", Key.X),
        (EditorTool.Crop, SymbolRegular.Crop24, "Crop", Key.C),
    ];

    private readonly EditorDocumentSource _source;
    private readonly EditorService _service;
    private readonly SettingsStore _settings;
    private readonly AnnotationEditor _editor;
    private readonly EditorViewModel _viewModel;
    private readonly EditorCanvas _canvas;
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = [];

    private TextBox? _textBox;
    private TextItem? _textItem;
    private bool _spaceHeld;
    private Point? _panFrom;
    private Point _panScrollFrom;
    private bool _closingConfirmed;

    public EditorWindow(EditorDocumentSource source, EditorService service, ThemeController theme, SettingsStore settings)
    {
        _source = source;
        _service = service;
        _settings = settings;
        _editor = new AnnotationEditor(source.Document, settings.Current.Editor.ToolStyles);
        _viewModel = new EditorViewModel(_editor);
        DataContext = _viewModel;

        InitializeComponent();
        theme.Attach(this);

        _canvas = new EditorCanvas(_editor, source.Original);
        CanvasHost.Children.Insert(0, _canvas);
        BuildToolButtons();

        _editor.TextEditRequested += (_, item) => OpenTextBox(item);
        _editor.TextEditEnding += (_, _) => CommitTextBox();
        _editor.Changed += (_, _) => OnEditorChanged();
        _editor.History.Changed += (_, _) => UpdateTitle();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Scroller.PreviewMouseWheel += OnScrollerWheel;
        Scroller.PreviewMouseLeftButtonDown += OnPanStart;
        Scroller.PreviewMouseMove += OnPanMove;
        Scroller.PreviewMouseLeftButtonUp += OnPanEnd;
        Loaded += (_, _) =>
        {
            FitToWindow(initial: true);
            _canvas.Focus();
        };
        Closing += OnClosing;
        Closed += (_, _) => RememberToolStyles();

        SizeText.Text = $"{source.Original.Width} × {source.Original.Height} px";
        OnEditorChanged();
        UpdateTitle();
    }

    public EditorDocumentSource Source => _source;

    public AnnotationEditor Editor => _editor;

    /// <summary>Picks a starting window size that shows the image at 100 % when it fits on the screen.</summary>
    public void SizeToImage()
    {
        var work = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var wanted = new Size((_source.Original.Width / dpi) + 120, (_source.Original.Height / dpi) + 230);
        Width = Math.Clamp(wanted.Width, MinWidth, work.Width * 0.9);
        Height = Math.Clamp(wanted.Height, MinHeight, work.Height * 0.9);
    }

    // ------------------------------------------------------------------------------------------------- tools

    private void BuildToolButtons()
    {
        foreach (var (tool, icon, name, key) in Tools)
        {
            var button = new ToggleButton
            {
                Content = new SymbolIcon { Symbol = icon, FontSize = 18 },
                ToolTip = $"{name} ({key})",
                Margin = new Thickness(0, 0, 2, 0),
                Padding = new Thickness(8, 6, 8, 6),
                Focusable = false,
            };
            button.Click += (_, _) => _editor.Tool = tool;
            _toolButtons[tool] = button;
            ToolButtons.Children.Add(button);
        }
    }

    private void OnEditorChanged()
    {
        foreach (var (tool, button) in _toolButtons)
        {
            button.IsChecked = tool == _editor.Tool;
        }

        ResetCropButton.Visibility = _editor.Tool == EditorTool.Crop || _editor.Document.Crop is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateZoomText();
    }

    private void UpdateTitle()
    {
        var title = $"{_source.DisplayName}{(_editor.History.IsDirty ? " •" : string.Empty)} - Grably";
        Title = title;
        WindowTitleBar.Title = title;
    }

    // ------------------------------------------------------------------------------------------------- text

    private void OpenTextBox(TextItem item)
    {
        CommitTextBox();
        AnnotationRenderer.Measure(item, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var padding = Math.Round(item.Style.FontSize * 0.25);
        var at = _canvas.ToDip(item.Position.Offset(padding, padding));
        var stroke = item.Style.Stroke;
        var box = new TextBox
        {
            Text = item.Text,
            FontFamily = new FontFamily(item.Style.FontFamily),
            FontSize = Math.Max(6, item.Style.FontSize * _canvas.Scale),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(stroke.R, stroke.G, stroke.B)),
            Background = item.Background is { } bg ? new SolidColorBrush(Color.FromArgb(bg.A, bg.R, bg.G, bg.B)) : Brushes.Transparent,
            CaretBrush = new SolidColorBrush(Color.FromRgb(stroke.R, stroke.G, stroke.B)),
            BorderBrush = new SolidColorBrush(ThemeController.Accent),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 60,
            TextWrapping = item.Width is null ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Width = item.Width is { } w ? ((w - (2 * padding)) * _canvas.Scale) + TextBoxInset.Left + TextBoxInset.Right : double.NaN,
        };
        box.Style = null; // the plain box lines up with the rendered text; the Fluent style adds padding and corners

        // A plain TextBox draws its text inside a 1 DIP border plus a 2 DIP horizontal inset; offset by that so the
        // characters sit exactly where the annotation is drawn.
        Canvas.SetLeft(box, at.X - TextBoxInset.Left);
        Canvas.SetTop(box, at.Y - TextBoxInset.Top);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                CommitTextBox();
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => CommitTextBox();

        _textBox = box;
        _textItem = item;
        TextLayer.Children.Add(box);
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitTextBox()
    {
        if (_textBox is not { } box || _textItem is not { } item)
        {
            return;
        }

        _textBox = null;
        _textItem = null;
        TextLayer.Children.Remove(box);
        _editor.SetText(item, box.Text);
        _canvas.Focus();
    }

    // ------------------------------------------------------------------------------------------------- keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (_textBox is not null && _textBox.IsKeyboardFocusWithin)
        {
            // Typing into a text annotation: only saving reaches the editor (after committing the text).
            if (ctrl && key == Key.S)
            {
                e.Handled = true;
                CommitTextBox();
                if (shift)
                {
                    SaveAs();
                }
                else
                {
                    Save();
                }
            }

            return;
        }

        var typingElsewhere = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        if (typingElsewhere && ctrl && key is Key.Z or Key.Y or Key.A or Key.X or Key.C or Key.V)
        {
            return; // the hex box's own editing keys
        }

        if (ctrl)
        {
            e.Handled = true;
            switch (key)
            {
                case Key.Z when shift:
                case Key.Y:
                    _editor.Redo();
                    break;
                case Key.Z:
                    _editor.Undo();
                    break;
                case Key.C:
                    Copy();
                    break;
                case Key.V:
                    Paste();
                    break;
                case Key.S when shift:
                    SaveAs();
                    break;
                case Key.S:
                    Save();
                    break;
                case Key.D:
                    _editor.DuplicateSelected();
                    break;
                case Key.O:
                    Open();
                    break;
                case Key.D0 or Key.NumPad0:
                    FitToWindow(initial: false);
                    break;
                case Key.D1 or Key.NumPad1:
                    SetZoom(1);
                    break;
                case Key.OemPlus or Key.Add:
                    SetZoom(_canvas.Zoom * 1.25);
                    break;
                case Key.OemMinus or Key.Subtract:
                    SetZoom(_canvas.Zoom / 1.25);
                    break;
                case Key.OemCloseBrackets:
                    _editor.Reorder(+1, toEnd: shift);
                    break;
                case Key.OemOpenBrackets:
                    _editor.Reorder(-1, toEnd: shift);
                    break;
                default:
                    e.Handled = false;
                    break;
            }

            return;
        }

        if (typingElsewhere || Keyboard.FocusedElement is Slider)
        {
            return; // hex box, sliders: leave their keys alone
        }

        var step = shift ? 10 : 1;
        e.Handled = true;
        switch (key)
        {
            case Key.Delete or Key.Back:
                _editor.DeleteSelected();
                break;
            case Key.Left:
                _editor.Nudge(-step, 0);
                break;
            case Key.Right:
                _editor.Nudge(step, 0);
                break;
            case Key.Up:
                _editor.Nudge(0, -step);
                break;
            case Key.Down:
                _editor.Nudge(0, step);
                break;
            case Key.Escape when _editor.Selected is not null:
                _editor.Select(null);
                break;
            case Key.Escape:
                _editor.Tool = EditorTool.Select;
                break;
            case Key.Space:
                if (!_spaceHeld)
                {
                    _spaceHeld = true;
                    Scroller.Cursor = Cursors.Hand;
                }

                break;
            default:
                var tool = Tools.FirstOrDefault(t => t.Key == key);
                if (tool.Name is not null && Keyboard.Modifiers == ModifierKeys.None)
                {
                    _editor.Tool = tool.Tool;
                }
                else
                {
                    e.Handled = false;
                }

                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            Scroller.Cursor = null;
        }
    }

    // ------------------------------------------------------------------------------------------------- zoom and pan

    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        SetZoom(e.Delta > 0 ? _canvas.Zoom * 1.25 : _canvas.Zoom / 1.25, e.GetPosition(Scroller));
    }

    /// <summary>Changes the zoom, keeping the image point under <paramref name="anchor"/> (viewport coordinates) still.</summary>
    private void SetZoom(double zoom, Point? anchor = null)
    {
        CommitTextBox(); // the box is positioned for the old zoom
        var viewportAnchor = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var before = _canvas.ToImage(Scroller.TranslatePoint(viewportAnchor, _canvas));
        _canvas.Zoom = zoom;
        Scroller.UpdateLayout();
        var after = _canvas.TranslatePoint(_canvas.ToDip(before), CanvasHost);
        Scroller.ScrollToHorizontalOffset(after.X + CanvasHost.Margin.Left - viewportAnchor.X);
        Scroller.ScrollToVerticalOffset(after.Y + CanvasHost.Margin.Top - viewportAnchor.Y);
        UpdateZoomText();
    }

    private void FitToWindow(bool initial)
    {
        Scroller.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var view = _canvas.View;
        var width = Math.Max(50, Scroller.ViewportWidth - (2 * CanvasHost.Margin.Left));
        var height = Math.Max(50, Scroller.ViewportHeight - (2 * CanvasHost.Margin.Top));
        var fit = Math.Min(width * dpi / view.Width, height * dpi / view.Height);
        _canvas.Zoom = initial ? Math.Min(1, fit) : fit;
        UpdateZoomText();
    }

    private void UpdateZoomText() => ZoomText.Text = $"{_canvas.Zoom * 100:0}%";

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
        if (!_spaceHeld)
        {
            return;
        }

        _panFrom = e.GetPosition(Scroller);
        _panScrollFrom = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Scroller.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from)
        {
            return;
        }

        var now = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_panScrollFrom.X - (now.X - from.X));
        Scroller.ScrollToVerticalOffset(_panScrollFrom.Y - (now.Y - from.Y));
        e.Handled = true;
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
        if (_panFrom is null)
        {
            return;
        }

        _panFrom = null;
        Scroller.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------------------- commands

    private void Copy()
    {
        if (_editor.Selected is { } item)
        {
            var data = new DataObject();
            data.SetData(ItemClipboardFormat, AnnotationJson.Serialize(item));
            Clipboard.SetDataObject(data, copy: true);
            return;
        }

        _service.CopyImage(_source, _editor.Document);
    }

    private void Paste()
    {
        if (Clipboard.ContainsData(ItemClipboardFormat) && Clipboard.GetData(ItemClipboardFormat) is string json)
        {
            try
            {
                _editor.Paste(AnnotationJson.DeserializeItem(json));
            }
            catch (System.Text.Json.JsonException)
            {
                // Not ours after all; ignore.
            }

            return;
        }

        if (Clipboard.ContainsImage())
        {
            _service.OpenClipboardImage();
        }
    }

    private bool Save()
    {
        CommitTextBox();
        if (_source.ImagePath is null)
        {
            return SaveAs();
        }

        if (!_service.Save(_source, _editor.Document))
        {
            return false;
        }

        _editor.History.MarkSaved();
        return true;
    }

    private bool SaveAs()
    {
        CommitTextBox();
        // An opened file is never overwritten silently: suggest a new name next to it.
        var basis = _source.ImagePath ?? _source.OpenedFrom;
        var suggested = Path.GetFileNameWithoutExtension(_source.DisplayName)
            + (_source.ImagePath is null && _source.OpenedFrom is not null ? " (annotated)" : string.Empty);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save image as",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = suggested,
            InitialDirectory = basis is null ? null : Path.GetDirectoryName(basis),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        var becameDocumentFile = _source.ImagePath is null;
        if (!_service.SaveAs(_source, _editor.Document, dialog.FileName))
        {
            return false;
        }

        if (becameDocumentFile)
        {
            _editor.History.MarkSaved();
        }

        UpdateTitle();
        return true;
    }

    private void Open()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an image or Grably project",
            Filter = "Images and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.grably|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _service.OpenFile(dialog.FileName);
        }
    }

    /// <summary>
    /// Asks whether to save unsaved changes. Returns true when the window may close (saved, discarded, or nothing to
    /// save); false when the user cancelled or saving did not happen.
    /// </summary>
    public bool ConfirmClose() => _closingConfirmed || CloseCoordinator.ConfirmAll([this]);

    bool IUnsavedWork.HasUnsavedChanges
    {
        get
        {
            CommitTextBox();
            return _editor.History.IsDirty;
        }
    }

    SaveChoice IUnsavedWork.AskToSave()
    {
        Activate();
        var answer = System.Windows.MessageBox.Show(
            this, $"Save your changes to {_source.DisplayName}?", "Grably", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveChoice.Save,
            MessageBoxResult.No => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }

    bool IUnsavedWork.Save() => Save();

    void IUnsavedWork.KeepForLater() => _service.KeepForLater(_source, _editor.Document);

    void IUnsavedWork.AllowClose() => _closingConfirmed = true;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmClose())
        {
            e.Cancel = true;
        }
    }

    private void RememberToolStyles()
    {
        var settings = _settings.Current;
        if (settings.Editor.ToolStyles.Count == _editor.ToolStyles.Count
            && settings.Editor.ToolStyles.All(p => _editor.ToolStyles.TryGetValue(p.Key, out var s) && s == p.Value))
        {
            return;
        }

        settings.Editor.ToolStyles = new Dictionary<EditorTool, AnnotationStyle>(_editor.ToolStyles);
        _settings.Save(settings);
    }

    // ------------------------------------------------------------------------------------------------- toolbar events

    private void Undo_Click(object sender, RoutedEventArgs e) => _editor.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => _editor.Redo();

    private void Copy_Click(object sender, RoutedEventArgs e) => _service.CopyImage(_source, _editor.Document);

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void Open_Click(object sender, RoutedEventArgs e) => Open();

    private void BringForward_Click(object sender, RoutedEventArgs e) => _editor.Reorder(+1);

    private void SendBackward_Click(object sender, RoutedEventArgs e) => _editor.Reorder(-1);

    private void Duplicate_Click(object sender, RoutedEventArgs e) => _editor.DuplicateSelected();

    private void Delete_Click(object sender, RoutedEventArgs e) => _editor.DeleteSelected();

    private void ResetCrop_Click(object sender, RoutedEventArgs e) => _editor.ResetCrop();

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_canvas.Zoom * 1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_canvas.Zoom / 1.25);

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow(initial: false);

    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Color color })
        {
            _viewModel.SetColor(color);
        }
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            HexBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            _canvas.Focus();
        }
    }

    private void DragButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var file = _service.WriteTemporaryCopy(_source, _editor.Document);
        var data = new DataObject();
        data.SetFileDropList([file]);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }
}
