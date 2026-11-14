namespace Grably.App.Shell;

/// <summary>
/// Short messages for the user ("Screenshot saved"). The tray icon shows them; anything can raise them without
/// depending on the tray (which would create a dependency cycle through <see cref="AppCommands"/>).
/// </summary>
public sealed class UserNotifications
{
    public event EventHandler<string>? Requested;

    public void Show(string message) => Requested?.Invoke(this, message);
}
