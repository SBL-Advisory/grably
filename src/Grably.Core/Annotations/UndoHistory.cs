namespace Grably.Core.Annotations;

/// <summary>
/// Snapshot-based undo: after every completed change the whole document is serialised (documents are small), so
/// undo is simply going back to an earlier snapshot. Hard to get wrong, and redo comes for free.
/// </summary>
public sealed class UndoHistory
{
    public const int MaxSnapshots = 200;

    private readonly List<string> _snapshots = [];
    private int _index;

    public UndoHistory(AnnotationDocument document) => Reset(document);

    public event EventHandler? Changed;

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index < _snapshots.Count - 1;

    /// <summary>True when the document differs from the last time <see cref="MarkSaved"/> was called.</summary>
    public bool IsDirty => _savedSnapshot != _snapshots[_index];

    private string? _savedSnapshot;
    private string? _lastMergeKey;

    /// <summary>Starts a fresh history at <paramref name="document"/> (opening a document).</summary>
    public void Reset(AnnotationDocument document)
    {
        _lastMergeKey = null;
        _snapshots.Clear();
        _snapshots.Add(AnnotationJson.Serialize(document));
        _index = 0;
        _savedSnapshot = _snapshots[0];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the document after a completed change. Does nothing if nothing changed.</summary>
    /// <param name="document">The document as it is now.</param>
    /// <param name="mergeKey">
    /// Consecutive commits with the same key replace each other instead of piling up (e.g. every step of dragging a
    /// thickness slider becomes one undo step). Null never merges.
    /// </param>
    public void Commit(AnnotationDocument document, string? mergeKey = null)
    {
        var snapshot = AnnotationJson.Serialize(document);
        if (snapshot == _snapshots[_index])
        {
            return;
        }

        var merge = mergeKey is not null && mergeKey == _lastMergeKey && _index > 0 && _index == _snapshots.Count - 1
            && _snapshots[_index] != _savedSnapshot;
        _lastMergeKey = mergeKey;
        if (merge)
        {
            _snapshots[_index] = snapshot;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _snapshots.RemoveRange(_index + 1, _snapshots.Count - _index - 1);
        _snapshots.Add(snapshot);
        if (_snapshots.Count > MaxSnapshots)
        {
            if (_savedSnapshot == _snapshots[0])
            {
                _savedSnapshot = null; // the saved state fell off the end; treat as dirty from now on
            }

            _snapshots.RemoveAt(0);
        }

        _index = _snapshots.Count - 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AnnotationDocument Undo()
    {
        _lastMergeKey = null;
        if (CanUndo)
        {
            _index--;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Current();
    }

    public AnnotationDocument Redo()
    {
        _lastMergeKey = null;
        if (CanRedo)
        {
            _index++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Current();
    }

    /// <summary>A fresh copy of the current state.</summary>
    public AnnotationDocument Current() => AnnotationJson.Deserialize(_snapshots[_index]);

    public void MarkSaved()
    {
        _savedSnapshot = _snapshots[_index];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
