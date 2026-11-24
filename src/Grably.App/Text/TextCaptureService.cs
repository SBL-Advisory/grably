using System.Windows;
using Grably.App.Capture;
using Grably.App.Shell;
using Grably.Core.Imaging;
using Grably.Platform.Text;
using Microsoft.Extensions.Logging;

namespace Grably.App.Text;

/// <summary>
/// "Capture text" and the editor's "Copy text": recognise the text in an area or image and put it on the clipboard.
/// Only line counts are logged, never the text.
/// </summary>
public sealed class TextCaptureService(
    ScreenshotService screenshots, OcrService ocr, UserNotifications notifications, ILogger<TextCaptureService> logger)
{
    /// <summary>Lets the user pick an area of the screen and copies the text in it.</summary>
    public Task CaptureAsync() => screenshots.RunExclusiveAsync(async () =>
    {
        if (!ocr.IsAvailable)
        {
            notifications.Show(new OcrUnavailableException().Message);
            return;
        }

        if (await screenshots.PickAreaAsync("Drag over the text to copy, or click a window. Esc cancels.") is { } picked)
        {
            await CopyTextAsync(picked.Image, "Text capture");
        }
    });

    /// <summary>Copies the text in <paramref name="image"/> and says how much was found.</summary>
    public async Task CopyTextAsync(PixelBuffer image, string what)
    {
        try
        {
            var outcome = await Task.Run(() => ocr.RecognizeAsync(image));
            logger.LogInformation("{What} found {Lines} line(s)", what, outcome.Lines.Count);
            if (outcome.Lines.Count == 0)
            {
                notifications.Show("No text was found.");
                return;
            }

            try
            {
                Clipboard.SetText(outcome.Text); // WPF retries for about a second if another app holds the clipboard
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                notifications.Show("The text was read, but the clipboard is busy. Try again.");
                return;
            }

            notifications.Show(outcome.Lines.Count == 1 ? "Copied 1 line of text" : $"Copied {outcome.Lines.Count} lines of text");
        }
        catch (OcrUnavailableException ex)
        {
            notifications.Show(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "{What} failed", what);
            notifications.Show($"The text could not be read: {ex.Message}");
        }
    }
}
