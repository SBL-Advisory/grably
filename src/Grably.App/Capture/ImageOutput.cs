using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Grably.Core.Settings;

namespace Grably.App.Capture;

/// <summary>Writes images to the clipboard and to files.</summary>
public static class ImageOutput
{
    /// <summary>
    /// Puts the image on the clipboard as a bitmap (for every app) and as PNG (keeps transparency, preferred by
    /// browsers and Office). WPF already retries for about a second when another app holds the clipboard open.
    /// </summary>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">The clipboard stayed busy.</exception>
    public static void CopyToClipboard(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);
        var png = new MemoryStream();
        Encode(image, ImageFormat.Png, 100, png);
        png.Position = 0;
        data.SetData("PNG", png, autoConvert: false);
        Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>Encodes to a temporary file next to <paramref name="path"/> and then moves it into place.</summary>
    public static void Save(BitmapSource image, string path, ImageFormat format, int jpegQuality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            {
                Encode(image, format, jpegQuality, file);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static string Extension(ImageFormat format) => format == ImageFormat.Jpeg ? "jpg" : "png";

    private static void Encode(BitmapSource image, ImageFormat format, int jpegQuality, Stream output)
    {
        BitmapEncoder encoder = format == ImageFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; a leftover .tmp file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
