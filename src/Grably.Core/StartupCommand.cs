namespace Grably.Core;

/// <summary>The command Windows runs at sign-in to start Grably (the per-user Run value).</summary>
public static class StartupCommand
{
    /// <summary>Starts <paramref name="executable"/> in the tray, without opening a window.</summary>
    public static string For(string executable) => $"\"{executable}\" --background";

    /// <summary>
    /// The executable a Run command starts: the quoted part, or an unquoted path up to and including the first ".exe"
    /// that ends a word (a value edited by hand may have no quotes). Null when there is neither.
    /// </summary>
    public static string? Executable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        for (var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); exe > 0;
             exe = text.IndexOf(".exe", exe + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = exe + 4;
            if (end == text.Length || char.IsWhiteSpace(text[end]))
            {
                return text[..end];
            }
        }

        return null;
    }
}
