using System.IO;
using Grably.App.Library;
using Grably.Core.Settings;
using Grably.Platform.Text;
using Microsoft.Extensions.Logging;

namespace Grably.App.Text;

/// <summary>
/// Reads the text in library screenshots in the background (newest first, one at a time, with pauses), so library
/// search finds words inside them. Runs when the library changes and at startup; can be turned off in Settings.
/// Images are decoded strip by strip off the UI thread. Everything stays on this PC, and the text is never logged.
/// </summary>
public sealed class TextIndexer(LibraryService library, OcrService ocr, SettingsStore settings, ILogger<TextIndexer> logger) : IDisposable
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<long> _unreadable = []; // files that could not be read this session
    private int _running;
    private volatile bool _again;

    public void Start()
    {
        library.Changed += (_, _) => Kick();
        settings.Saved += (_, _) => Kick();
        Kick();
    }

    /// <summary>Starts a pass unless one is running (then that pass looks again when it finishes).</summary>
    public void Kick()
    {
        if (!settings.Current.Screenshot.MakeTextSearchable || _stop.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            _again = true;
            return;
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>Stops at the next pause. The token source is left for the running pass to finish with.</summary>
    public void Dispose() => _stop.Cancel();

    private async Task RunAsync()
    {
        var read = 0;
        try
        {
            if (!ocr.IsAvailable)
            {
                logger.LogInformation("Screenshot text is not indexed: no OCR language is installed");
                return;
            }

            while (!_stop.IsCancellationRequested && settings.Current.Screenshot.MakeTextSearchable)
            {
                var batch = library.Repository.WithoutText(10, _unreadable);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var item in batch)
                {
                    await Task.Delay(Pause, _stop.Token); // stay out of the way of whatever the user is doing
                    string text;
                    try
                    {
                        text = (await ocr.RecognizeFileAsync(item.FilePath!, _stop.Token)).Text;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OcrUnavailableException)
                    {
                        // Gone, locked, not an image, or too big: leave it for this session (the next run tries again).
                        logger.LogDebug(ex, "Could not read the text of library item {Id}", item.Id);
                        _unreadable.Add(item.Id);
                        continue;
                    }

                    // Only if the item is unchanged since it was read: a save meanwhile asks for a fresh read.
                    if (library.Repository.SetText(item, text))
                    {
                        read++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Reading screenshot text stopped");
            library.ReportError(ex);
        }
        finally
        {
            if (read > 0)
            {
                logger.LogInformation("Read the text of {Count} screenshot(s) for search", read);
            }

            Volatile.Write(ref _running, 0);
            if (_again)
            {
                _again = false;
                Kick();
            }
        }
    }
}
