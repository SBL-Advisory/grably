using System.Runtime.InteropServices;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Capture;

/// <summary>
/// Still screenshots through GDI BitBlt: fast, works for every monitor at once and includes layered windows
/// (menus, tooltips) thanks to CAPTUREBLT. The process must be per-monitor DPI aware so coordinates are physical.
/// </summary>
public static class ScreenGrabber
{
    /// <summary>Copies <paramref name="area"/> of the virtual desktop into an opaque BGRA buffer.</summary>
    /// <param name="includeCursor">Draw the mouse pointer into the image where it currently is.</param>
    public static unsafe PixelBuffer Grab(PixelRect area, bool includeCursor = false)
    {
        if (area.IsEmpty)
        {
            throw new ArgumentException("The capture area is empty.", nameof(area));
        }

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            throw new InvalidOperationException("Could not get the screen device context.");
        }

        var memory = PInvoke.CreateCompatibleDC(screen);
        if (memory.IsNull)
        {
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
            throw new InvalidOperationException("Could not create a memory device context.");
        }

        try
        {
            var header = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = area.Width,
                    biHeight = -area.Height, // negative: top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };

            void* bits;
            var bitmap = PInvoke.CreateDIBSection(memory, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (bitmap.IsNull)
            {
                throw new InvalidOperationException($"Could not allocate a {area.Width}x{area.Height} bitmap.");
            }

            var previous = PInvoke.SelectObject(memory, bitmap);
            try
            {
                // Fails on the secure desktop (lock screen, UAC prompt) or a disconnected remote session.
                var copied = PInvoke.BitBlt(memory, 0, 0, area.Width, area.Height, screen, area.X, area.Y,
                    ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
                if (!copied)
                {
                    throw new InvalidOperationException(
                        $"The screen could not be read (Win32 error {Marshal.GetLastPInvokeError()}). Is the desktop locked?");
                }

                if (includeCursor)
                {
                    DrawCursor(memory, area.Location);
                }

                PInvoke.GdiFlush(); // make sure GDI finished writing before the bits are read

                var result = new PixelBuffer(area.Width, area.Height);
                new ReadOnlySpan<byte>(bits, result.Pixels.Length).CopyTo(result.Pixels);
                MakeOpaque(result.Pixels); // GDI leaves the alpha byte undefined (usually 0)
                return result;
            }
            finally
            {
                // A bitmap still selected into a DC cannot be deleted; deselect it on every path.
                PInvoke.SelectObject(memory, previous);
                PInvoke.DeleteObject(bitmap);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    private static unsafe void DrawCursor(HDC target, PixelPoint origin)
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(ref info) || (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || info.hCursor.IsNull)
        {
            return;
        }

        var hotspotX = 0;
        var hotspotY = 0;
        ICONINFO iconInfo;
        if (PInvoke.GetIconInfo((HICON)info.hCursor.Value, &iconInfo))
        {
            hotspotX = (int)iconInfo.xHotspot;
            hotspotY = (int)iconInfo.yHotspot;
            if (!iconInfo.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmMask);
            }

            if (!iconInfo.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmColor);
            }
        }

        PInvoke.DrawIconEx(target,
            info.ptScreenPos.X - hotspotX - origin.X,
            info.ptScreenPos.Y - hotspotY - origin.Y,
            (HICON)info.hCursor.Value, 0, 0, 0, HBRUSH.Null, DI_FLAGS.DI_NORMAL);
    }

    private static void MakeOpaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }
    }
}
