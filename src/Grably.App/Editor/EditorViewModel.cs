using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Grably.Core.Annotations;

namespace Grably.App.Editor;

/// <summary>
/// The property bar: shows the style of the selection (or of the current tool) and writes changes back through
/// <see cref="AnnotationEditor.ApplyStyle"/>, so every change is undoable.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    public static readonly Color[] Swatches =
    [
        Color.FromRgb(0xE8, 0x11, 0x23), Color.FromRgb(0xFF, 0x8C, 0x00), Color.FromRgb(0xFF, 0xE6, 0x00),
        Color.FromRgb(0x10, 0x89, 0x3E), Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x88, 0x17, 0x98),
        Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    private readonly AnnotationEditor _editor;
    private bool _syncing;

    public EditorViewModel(AnnotationEditor editor)
    {
        _editor = editor;
        _editor.Changed += (_, _) => Sync();
        _editor.History.Changed += (_, _) => Sync();
        Sync();
    }

    public AnnotationEditor Editor => _editor;

    public IReadOnlyList<Color> Palette => Swatches;

    /// <summary>Colours used this session that are not in the palette, newest first.</summary>
    public ObservableCollection<Color> RecentColors { get; } = [];

    [ObservableProperty]
    public partial Color StrokeColor { get; set; }

    [ObservableProperty]
    public partial string HexColor { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Thickness { get; set; }

    [ObservableProperty]
    public partial double Opacity { get; set; }

    [ObservableProperty]
    public partial bool Shadow { get; set; }

    [ObservableProperty]
    public partial bool Filled { get; set; }

    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial double Strength { get; set; }

    [ObservableProperty]
    public partial bool ShowThickness { get; set; }

    [ObservableProperty]
    public partial bool ShowFill { get; set; }

    [ObservableProperty]
    public partial bool ShowFontSize { get; set; }

    [ObservableProperty]
    public partial bool ShowStrength { get; set; }

    [ObservableProperty]
    public partial bool ShowColor { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial bool CanUndo { get; set; }

    [ObservableProperty]
    public partial bool CanRedo { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public void SetColor(Color color)
    {
        StrokeColor = color;
        if (!Swatches.Contains(color))
        {
            RecentColors.Remove(color);
            RecentColors.Insert(0, color);
            while (RecentColors.Count > 6)
            {
                RecentColors.RemoveAt(RecentColors.Count - 1);
            }
        }
    }

    partial void OnStrokeColorChanged(Color value)
    {
        HexColor = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        Apply(s => s with
        {
            Stroke = new Rgba(value.R, value.G, value.B),
            Fill = s.Fill is null ? null : new Rgba(value.R, value.G, value.B, 0x59),
        }, "color");
    }

    partial void OnHexColorChanged(string value)
    {
        if (!_syncing && Rgba.TryParse(value, out var c) && c.A == 255)
        {
            var color = Color.FromRgb(c.R, c.G, c.B);
            if (color != StrokeColor)
            {
                SetColor(color);
            }
        }
    }

    partial void OnThicknessChanged(double value) => Apply(s => s with { Thickness = Math.Round(value) }, "thickness");

    partial void OnOpacityChanged(double value) => Apply(s => s with { Opacity = Math.Round(value, 2) }, "opacity");

    partial void OnShadowChanged(bool value) => Apply(s => s with { Shadow = value }, "shadow");

    partial void OnFilledChanged(bool value) =>
        Apply(s => s with { Fill = value ? s.Stroke.WithAlpha(0x59) : null }, "fill");

    partial void OnFontSizeChanged(double value) => Apply(s => s with { FontSize = Math.Round(value) }, "fontsize");

    partial void OnStrengthChanged(double value)
    {
        if (!_syncing)
        {
            _editor.SetRedactStrength((int)Math.Round(value));
        }
    }

    private void Apply(Func<AnnotationStyle, AnnotationStyle> change, string property)
    {
        if (!_syncing)
        {
            _editor.ApplyStyle(change, property);
        }
    }

    /// <summary>Pulls the current style and state from the editor without writing anything back.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var style = _editor.CurrentStyle;
            StrokeColor = Color.FromRgb(style.Stroke.R, style.Stroke.G, style.Stroke.B);
            HexColor = $"#{style.Stroke.R:X2}{style.Stroke.G:X2}{style.Stroke.B:X2}";
            Thickness = style.Thickness;
            Opacity = style.Opacity;
            Shadow = style.Shadow;
            Filled = style.Fill is not null;
            FontSize = style.FontSize;

            var kind = _editor.Selected switch
            {
                LineItem { Heads: ArrowHeads.None } => EditorTool.Line,
                LineItem => EditorTool.Arrow,
                RectangleItem => EditorTool.Rectangle,
                EllipseItem => EditorTool.Ellipse,
                StrokeItem { Kind: StrokeKind.Highlighter } => EditorTool.Highlighter,
                StrokeItem => EditorTool.Pen,
                TextItem => EditorTool.Text,
                StepItem => EditorTool.Step,
                RedactItem { Mode: RedactMode.Blur } => EditorTool.Blur,
                RedactItem => EditorTool.Pixelate,
                _ => _editor.Tool,
            };
            ShowColor = kind is not (EditorTool.Blur or EditorTool.Pixelate or EditorTool.Crop);
            ShowThickness = kind is EditorTool.Arrow or EditorTool.Line or EditorTool.Rectangle or EditorTool.Ellipse
                or EditorTool.Pen or EditorTool.Highlighter or EditorTool.Select;
            ShowFill = kind is EditorTool.Rectangle or EditorTool.Ellipse;
            ShowFontSize = kind is EditorTool.Text or EditorTool.Step;
            ShowStrength = _editor.Selected is RedactItem;
            Strength = _editor.Selected is RedactItem redact ? redact.Strength : 8;

            HasSelection = _editor.Selected is not null;
            CanUndo = _editor.History.CanUndo;
            CanRedo = _editor.History.CanRedo;
            IsDirty = _editor.History.IsDirty;
        }
        finally
        {
            _syncing = false;
        }
    }
}
