namespace Grably.App.Editor;

public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Something that may hold unsaved work and must not lose it when the app exits or Windows signs out.</summary>
public interface IUnsavedWork
{
    bool HasUnsavedChanges { get; }

    /// <summary>Asks the user (Save / Don't save / Cancel).</summary>
    SaveChoice AskToSave();

    /// <summary>Saves normally; false if it did not happen (cancelled dialog, error).</summary>
    bool Save();

    /// <summary>Keeps the work somewhere safe without asking (sign-out): never overwrites the user's files.</summary>
    void KeepForLater();

    /// <summary>From now on, closing must not ask again (the decision was made for it).</summary>
    void AllowClose();
}

/// <summary>The rules for closing editors with unsaved work, kept apart from WPF so they can be tested.</summary>
public static class CloseCoordinator
{
    /// <summary>
    /// Asks about every editor with unsaved work, in order. Only when none is cancelled (and every requested save
    /// worked) are they all allowed to close; otherwise nothing is allowed and the caller keeps everything open,
    /// so an earlier "Don't save" never sticks to a window that stays open.
    /// </summary>
    public static bool ConfirmAll(IEnumerable<IUnsavedWork> editors)
    {
        var list = editors.ToList();
        foreach (var editor in list.Where(e => e.HasUnsavedChanges))
        {
            switch (editor.AskToSave())
            {
                case SaveChoice.Cancel:
                    return false;
                case SaveChoice.Save when !editor.Save():
                    return false;
            }
        }

        foreach (var editor in list)
        {
            editor.AllowClose();
        }

        return true;
    }

    /// <summary>Windows is signing out: no questions, keep unsaved work, then let everything close.</summary>
    public static void SessionEnding(IEnumerable<IUnsavedWork> editors)
    {
        foreach (var editor in editors.ToList())
        {
            if (editor.HasUnsavedChanges)
            {
                editor.KeepForLater();
            }

            editor.AllowClose();
        }
    }
}
