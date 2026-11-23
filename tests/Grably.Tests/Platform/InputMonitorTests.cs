using Grably.Core.Hotkeys;
using Grably.Core.Recording;
using Grably.Platform.Input;

namespace Grably.Tests.Platform;

/// <summary>Real Raw Input with an injected key no app uses: needs an interactive desktop.</summary>
[Trait("Category", "Desktop")]
public class InputMonitorTests
{
    [Fact]
    public void A_pressed_key_arrives_with_its_modifiers_and_types_nothing()
    {
        using var monitor = new InputMonitor(mouse: false, keyboard: true);
        var seen = new List<(int Key, HotkeyModifiers Modifiers, KeyText Text)>();
        using var arrived = new ManualResetEventSlim();
        monitor.KeyDown += (key, modifiers, text) =>
        {
            lock (seen)
            {
                seen.Add((key, modifiers, text));
                if (key == 0x87)
                {
                    arrived.Set();
                }
            }
        };

        InputSimulator.SendGesture(HotkeyGesture.Parse("Ctrl+Alt+Shift+F24"));

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken), "F24 never arrived");
        lock (seen)
        {
            var f24 = seen.Single(s => s.Key == 0x87);
            Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, f24.Modifiers);
            Assert.Null(f24.Text.Typed); // F24 types nothing on any layout
            Assert.Equal("Ctrl+Alt+Shift+F24", KeystrokeFormatter.Format(f24.Key, f24.Modifiers, Grably.Core.Settings.KeystrokeMode.ShortcutsOnly, f24.Text));
        }
    }
}
