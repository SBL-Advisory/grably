using System.Numerics;
using Grably.Core.Geometry;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace Grably.Platform.Recording;

/// <summary>What to draw over one frame, in screen pixels (the compositor maps them into the frame).</summary>
public sealed record OverlayState
{
    /// <summary>Where the pointer is, for the highlight; null when the highlight is off.</summary>
    public PixelPoint? Cursor { get; init; }

    public IReadOnlyList<Ripple> Ripples { get; init; } = [];

    public KeystrokeCaption? Caption { get; init; }

    public WebcamFrame? Webcam { get; init; }

    /// <summary>The webcam bubble's place on screen.</summary>
    public PixelRect? WebcamRect { get; init; }

    public WebcamShape WebcamShape { get; init; } = WebcamShape.Circle;

    /// <summary>Show the camera like a mirror (what people expect of their own picture).</summary>
    public bool MirrorWebcam { get; init; } = true;
}

/// <summary>
/// Draws cursor effects, the keystroke caption and the webcam over a frame with Direct2D. Sizes are given in screen
/// pixels and scaled with the picture, so effects look the same whatever the output size.
/// </summary>
internal sealed class OverlayRenderer(ID2D1Factory1 factory) : IDisposable
{
    public static readonly Color4 HighlightColor = new(1f, 0.83f, 0f, 0.6f);
    public static readonly Color4 LeftClickColor = new(0x0E / 255f, 0x9A / 255f, 0xA7 / 255f, 1f);
    public static readonly Color4 RightClickColor = new(0xE5 / 255f, 0x38 / 255f, 0x3B / 255f, 1f);

    private readonly IDWriteFactory _write = DWrite.DWriteCreateFactory<IDWriteFactory>();
    private IDWriteTextFormat? _captionFormat;
    private float _captionSize;
    private ID2D1Bitmap1? _webcam;
    private long _webcamVersion = -1;

    /// <param name="map">Screen pixel to frame position.</param>
    /// <param name="scale">Frame pixels per screen pixel.</param>
    public void Draw(ID2D1DeviceContext dc, OverlayState state, Func<PixelPoint, Vector2> map, float scale, int frameWidth, int frameHeight)
    {
        DrawWebcam(dc, state, map, scale);
        if (state.Cursor is { } cursor)
        {
            // A ring around the pointer, not a disc over it: the pointer keeps its own colours.
            using var highlight = dc.CreateSolidColorBrush(HighlightColor);
            dc.DrawEllipse(new Ellipse(map(cursor), 24 * scale, 24 * scale), highlight, 6 * Math.Max(1, scale));
        }

        foreach (var ripple in state.Ripples)
        {
            var color = ripple.Button == MouseButtonKind.Right ? RightClickColor
                : ripple.Button == MouseButtonKind.Left ? LeftClickColor
                : new Color4(0.6f, 0.6f, 0.6f, 1f);
            using var ring = dc.CreateSolidColorBrush(new Color4(color.R, color.G, color.B, (float)(1 - ripple.Progress)));
            var radius = (float)(10 + (30 * ripple.Progress)) * scale;
            dc.DrawEllipse(new Ellipse(map(ripple.Position), radius, radius), ring, 3 * Math.Max(1, scale));
        }

        if (state.Caption is { } caption)
        {
            DrawCaption(dc, caption, frameWidth, frameHeight);
        }
    }

    public void Dispose()
    {
        _webcam?.Dispose();
        _captionFormat?.Dispose();
        _write.Dispose();
    }

    private void DrawWebcam(ID2D1DeviceContext dc, OverlayState state, Func<PixelPoint, Vector2> map, float scale)
    {
        if (state.Webcam is not { } frame || state.WebcamRect is not { } rect || rect.IsEmpty)
        {
            return;
        }

        if (_webcam is null || _webcam.PixelSize.Width != frame.Width || _webcam.PixelSize.Height != frame.Height)
        {
            _webcam?.Dispose();
            _webcam = dc.CreateBitmap(
                new SizeI(frame.Width, frame.Height), IntPtr.Zero, 0,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
            _webcam.CopyFromMemory(frame.Pixels, (uint)(frame.Width * 4));
            _webcamVersion = frame.Version;
        }
        else if (frame.Version != _webcamVersion)
        {
            _webcam.CopyFromMemory(frame.Pixels, (uint)(frame.Width * 4));
            _webcamVersion = frame.Version;
        }

        var topLeft = map(new PixelPoint(rect.X, rect.Y));
        var target = new Rect(topLeft.X, topLeft.Y, rect.Width * scale, rect.Height * scale);

        // Fill the shape: crop the camera picture to the shape's proportions, from the middle.
        var aspect = target.Width / target.Height;
        var sourceWidth = Math.Min(frame.Width, frame.Height * aspect);
        var sourceHeight = sourceWidth / aspect;
        var source = new Rect((frame.Width - sourceWidth) / 2, (frame.Height - sourceHeight) / 2, sourceWidth, sourceHeight);

        var radius = Math.Min(target.Width, target.Height) * 0.12f;
        using ID2D1Geometry mask = state.WebcamShape == WebcamShape.Circle
            ? factory.CreateEllipseGeometry(new Ellipse(new Vector2(target.X + (target.Width / 2), target.Y + (target.Height / 2)), target.Width / 2, target.Height / 2))
            : factory.CreateRoundedRectangleGeometry(new RoundedRectangle(new System.Drawing.RectangleF(target.X, target.Y, target.Width, target.Height), radius, radius));
        dc.PushLayer(
            new LayerParameters
            {
                ContentBounds = new Vortice.RawRectF(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
                GeometricMask = mask,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
            },
            null);
        if (state.MirrorWebcam)
        {
            dc.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(target.X + (target.Width / 2), 0));
        }

        dc.DrawBitmap(_webcam, target, 1, InterpolationMode.Linear, source, null);
        dc.Transform = Matrix3x2.Identity;
        dc.PopLayer();
        using var edge = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.85f));
        dc.DrawGeometry(mask, edge, 3 * Math.Max(1, scale));
    }

    /// <summary>The caption as a dark pill at the bottom middle of the frame.</summary>
    private void DrawCaption(ID2D1DeviceContext dc, KeystrokeCaption caption, int frameWidth, int frameHeight)
    {
        var size = Math.Clamp(frameHeight * 0.035f, 14f, 64f);
        if (_captionFormat is null || Math.Abs(_captionSize - size) > 0.5f)
        {
            _captionFormat?.Dispose();
            _captionFormat = _write.CreateTextFormat("Segoe UI", null, FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, size, "en-us");
            _captionSize = size;
        }

        using var layout = _write.CreateTextLayout(caption.Text, _captionFormat, frameWidth * 0.8f, size * 2);
        var metrics = layout.Metrics;
        var padX = size * 0.8f;
        var padY = size * 0.4f;
        var width = metrics.WidthIncludingTrailingWhitespace + (2 * padX);
        var height = metrics.Height + (2 * padY);
        var x = (frameWidth - width) / 2;
        var y = frameHeight - height - (frameHeight * 0.06f);
        using var background = dc.CreateSolidColorBrush(new Color4(0.08f, 0.08f, 0.08f, (float)(0.78 * caption.Opacity)));
        using var text = dc.CreateSolidColorBrush(new Color4(1, 1, 1, (float)caption.Opacity));
        dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, width, height), height / 2, height / 2), background);
        dc.DrawTextLayout(new Vector2(x + padX, y + padY), layout, text);
    }
}
