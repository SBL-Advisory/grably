using System.Diagnostics;
using Grably.App.Shell;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Imaging;
using Grably.Platform.Capture;
using Grably.Platform.Display;
using Grably.Platform.Hotkeys;
using Grably.Platform.Input;
using Microsoft.Extensions.Logging;

namespace Grably.App.Capture;

/// <summary>
/// Scrolling capture: the user picks a scrollable area, then Grably turns the mouse wheel over it, captures each
/// step once the screen has changed and settled, and stitches the steps into one tall image (<see cref="ScrollStitcher"/>).
/// It ends at the bottom of the page, on Esc or Stop, when the user moves the mouse, or at the maximum height, and
/// the image goes through the normal after-capture flow. Best effort: pages that load content lazily or animate can
/// defeat it, and the user is told when it stopped early.
/// </summary>
public sealed class ScrollingCaptureService(
    ScreenshotService screenshots, CaptureDelivery delivery, GlobalHotkeyService hotkeys, UserNotifications notifications,
    ILogger<ScrollingCaptureService> logger)
{
    private static readonly HotkeyGesture Escape = HotkeyGesture.Parse("Esc");

    /// <summary>How long the screen may take to start moving after a wheel turn (heavy pages repaint late).</summary>
    private static readonly TimeSpan ChangeLimit = TimeSpan.FromMilliseconds(600);

    private static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan SettleLimit = TimeSpan.FromMilliseconds(800);

    /// <summary>How far the pointer may drift (a resting hand) before the user counts as having moved it.</summary>
    private const int PointerTolerance = 24;

    private const int MaxNotches = 15;
    private const int MaxLossesInARow = 2;

    private enum Ending
    {
        PageEnded,
        Stopped,
        MaxHeight,
        MouseMoved,
        KeyHeld,
        NoMatch,
    }

    public Task CaptureAsync() => screenshots.RunExclusiveAsync(async () =>
    {
        var picked = await screenshots.PickAreaAsync("Select the area to scroll, or click a window. Esc cancels.");
        if (picked is null)
        {
            return;
        }

        if (picked.Area.Width < 64 || picked.Area.Height < 64)
        {
            notifications.Show("That area is too small for a scrolling capture.");
            return;
        }

        var (image, steps, ending) = await ScrollAndStitchAsync(picked.Area);
        var source = new CaptureSource(picked.Source?.Title, picked.Source?.ProcessName);
        delivery.Deliver(new CaptureResult(image, picked.Area, CaptureKind.Scrolling, source, picked.CapturedAt));

        var message = (steps, ending) switch
        {
            (0, Ending.PageEnded) => "The area did not scroll, so it was saved as a normal screenshot.",
            (_, Ending.MaxHeight) => $"The scrolling capture reached its maximum height of {ScrollStitcher.DefaultMaxHeight:N0} px.",
            (_, Ending.MouseMoved) => "The scrolling capture stopped early because the mouse moved. Keep it still until the capture ends.",
            (_, Ending.KeyHeld) => "The scrolling capture stopped early because a key was held down.",
            (_, Ending.NoMatch) => "The scrolling capture stopped early: the content changed too much between steps to join them.",
            _ => null,
        };
        if (message is not null)
        {
            notifications.Show(message);
        }
    });

    private async Task<(PixelBuffer Image, int Steps, Ending Ending)> ScrollAndStitchAsync(PixelRect area)
    {
        var stop = false;
        using var escape = hotkeys.RegisterTemporary(Escape, () => Volatile.Write(ref stop, true));
        var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(area.Center));
        var panel = new ScrollingCaptureWindow(area, monitor?.WorkArea ?? area);
        panel.Show();
        var frames = 1;
        var steps = 0;
        var ending = Ending.PageEnded;
        try
        {
            InputSimulator.MoveTo(area.Center);
            await Task.Delay(250);
            var anchor = ScreenPointer.Position(); // where it rests now, in case the hand nudged it right away
            var current = await SettleAsync(area, ScreenGrabber.Grab(area));
            var stitcher = new ScrollStitcher(current);
            var notches = 1;
            var perNotch = 0.0;
            var still = 0;
            var losses = 0;
            while (true)
            {
                if (Volatile.Read(ref stop) || panel.StopRequested)
                {
                    ending = Ending.Stopped;
                    break;
                }

                var pointer = ScreenPointer.Position();
                if (!area.Contains(pointer) || Math.Abs(pointer.X - anchor.X) > PointerTolerance || Math.Abs(pointer.Y - anchor.Y) > PointerTolerance)
                {
                    ending = Ending.MouseMoved;
                    break;
                }

                if (!await ModifiersReleasedAsync())
                {
                    ending = Ending.KeyHeld; // Ctrl or Shift would turn the wheel into zoom or sideways scrolling
                    break;
                }

                InputSimulator.Wheel(-notches);
                var frame = await GrabAfterScrollAsync(area, current);
                frames++;
                var hint = perNotch > 0 ? (int?)Math.Round(perNotch * notches) : null;
                var step = await Task.Run(() => stitcher.Add(frame, hint)); // comparing frames can take a moment
                if (step.Outcome == StitchOutcome.Appended)
                {
                    current = frame;
                    steps++;
                    still = 0;
                    losses = 0;
                    perNotch = (double)step.Offset / notches;

                    // Aim for about half the area per step: enough overlap to match, few steps.
                    notches = Math.Clamp((int)Math.Round(area.Height * 0.5 / perNotch), 1, MaxNotches);
                    panel.Update(stitcher.Height);
                }
                else if (step.Outcome == StitchOutcome.NoMovement)
                {
                    current = frame;
                    if (++still >= 2)
                    {
                        break; // the end of the page (asked twice, in case content was still loading)
                    }
                }
                else if (step.Outcome == StitchOutcome.Lost)
                {
                    // Scrolled past the overlap (or the content changed): go back and take a smaller step.
                    if (++losses > MaxLossesInARow || notches == 1)
                    {
                        ending = Ending.NoMatch;
                        break;
                    }

                    InputSimulator.Wheel(notches);
                    current = await GrabAfterScrollAsync(area, frame);
                    notches = Math.Max(1, notches / 2);
                }
                else
                {
                    ending = Ending.MaxHeight;
                    break;
                }
            }

            var image = stitcher.Result();
            logger.LogInformation(
                "Scrolling capture of {Width}x{Height}: {Frames} frame(s), {Steps} step(s), {Result} px tall, ended: {Ending}",
                area.Width, area.Height, frames, steps, image.Height, ending);
            return (image, steps, ending);
        }
        finally
        {
            panel.Close();
        }
    }

    /// <summary>Waits up to two seconds for the user to let go of Ctrl, Shift, Alt and Win.</summary>
    private static async Task<bool> ModifiersReleasedAsync()
    {
        for (var i = 0; i < 40 && InputSimulator.AnyModifierDown(); i++)
        {
            await Task.Delay(50);
        }

        return !InputSimulator.AnyModifierDown();
    }

    /// <summary>
    /// The area after a wheel turn: first wait for it to change from <paramref name="before"/> (an app may repaint
    /// late, and an unchanged grab must not pass for "the page ended"), then for it to stop changing.
    /// </summary>
    private static async Task<PixelBuffer> GrabAfterScrollAsync(PixelRect area, PixelBuffer before)
    {
        var clock = Stopwatch.StartNew();
        var frame = ScreenGrabber.Grab(area);
        while (Same(frame, before) && clock.Elapsed < ChangeLimit)
        {
            await Task.Delay(40);
            frame = ScreenGrabber.Grab(area);
        }

        return Same(frame, before) ? frame : await SettleAsync(area, frame);
    }

    /// <summary>Grabs until two grabs in a row are identical (smooth scrolling has finished), or a time limit.</summary>
    private static async Task<PixelBuffer> SettleAsync(PixelRect area, PixelBuffer first)
    {
        var clock = Stopwatch.StartNew();
        var previous = first;
        while (clock.Elapsed < SettleLimit)
        {
            await Task.Delay(SettleStep);
            var next = ScreenGrabber.Grab(area);
            if (Same(next, previous))
            {
                return next;
            }

            previous = next;
        }

        return previous;
    }

    private static bool Same(PixelBuffer a, PixelBuffer b) => a.Pixels.AsSpan().SequenceEqual(b.Pixels);
}
