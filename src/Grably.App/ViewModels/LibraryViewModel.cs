using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.Core.Capture;
using Grably.Core.Hotkeys;
using Grably.Core.Library;
using Grably.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Grably.App.ViewModels;

public enum LibraryFilter
{
    All,
    Screenshots,
    Recordings,
    Gifs,
}

/// <summary>The Library main window: the capture grid with search and filters, and the capture buttons.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppCommands _commands;
    private readonly SettingsStore _settings;
    private readonly LibraryService _library;
    private readonly LibraryActions _actions;
    private readonly ILogger<LibraryViewModel> _logger;
    private bool _loadFailed;
    private int _generation;
    private CancellationTokenSource? _fileCheck;
    private (string Search, LibraryFilter Filter)? _shown;

    /// <summary>Far beyond normal use; past it the grid says so and search still finds older captures.</summary>
    public const int MaxShown = 20_000;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public LibraryViewModel(
        AppCommands commands, SettingsStore settings, LibraryService library, LibraryActions actions, ILogger<LibraryViewModel> logger)
    {
        _commands = commands;
        _settings = settings;
        _library = library;
        _actions = actions;
        _logger = logger;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Refresh();
        };
        _library.Changed += OnLibraryChanged;
        Refresh();
    }

    /// <summary>Replaced on a new search or filter; updated in place otherwise, so selection and scrolling survive.</summary>
    [ObservableProperty]
    public partial ObservableCollection<LibraryItemViewModel> Items { get; private set; } = [];

    /// <summary>The library has more matching items than the grid shows.</summary>
    [ObservableProperty]
    public partial bool IsTruncated { get; set; }

    public string TruncatedText => $"Showing the newest {MaxShown:N0} captures. Search to find older ones.";

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LibraryFilter Filter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingText))]
    public partial int MissingCount { get; set; }

    public string MissingText => MissingCount == 1
        ? "1 item is no longer on disk. It was moved or deleted outside Grably."
        : $"{MissingCount} items are no longer on disk. They were moved or deleted outside Grably.";

    /// <summary>True when the library has nothing at all (not just nothing matching the search).</summary>
    [ObservableProperty]
    public partial bool IsLibraryEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    public string EmptyTitle => _loadFailed ? "The library could not be opened" : "No captures yet";

    public string EmptyHint
    {
        get
        {
            if (_loadFailed)
            {
                return "Your captures are still in their folders. Grably tries again each time this window is opened; details are in the log.";
            }

            var region = Describe(HotkeyAction.CaptureRegion);
            var record = Describe(HotkeyAction.ToggleRecording);
            return $"Press {region} to capture part of the screen, or {record} to start a recording. "
                + "Everything you capture shows up here.";
        }
    }

    /// <summary>Stops listening to the library (the window closed).</summary>
    public void Detach() => _library.Changed -= OnLibraryChanged;

    /// <summary>Reloads the grid from the library. Safe to call often (the window calls it when activated).</summary>
    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        var kind = Filter switch
        {
            LibraryFilter.Screenshots => LibraryItemKind.Screenshot,
            LibraryFilter.Recordings => LibraryItemKind.Recording,
            LibraryFilter.Gifs => LibraryItemKind.Gif,
            _ => (LibraryItemKind?)null,
        };

        IReadOnlyList<LibraryItem> found;
        try
        {
            found = _library.Repository.Query(new LibraryQuery(SearchText, kind, Limit: MaxShown));
            IsLibraryEmpty = _library.Repository.Count() == 0;
            _loadFailed = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not read the library");
            _library.ReportError(ex);
            found = [];
            IsLibraryEmpty = true;
            _loadFailed = true;
        }

        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint)); // the hotkeys it names may have changed in Settings

        // Keep the tiles of unchanged items (their selection, thumbnail and place in the scroll).
        var existing = Items.ToDictionary(t => t.Item.Id);
        var tiles = found.Select(item => existing.TryGetValue(item.Id, out var tile) && tile.Item == item ? tile : new LibraryItemViewModel(item)).ToList();
        if (_shown == (SearchText, Filter))
        {
            Sync(Items, tiles);
        }
        else
        {
            Items = new ObservableCollection<LibraryItemViewModel>(tiles);
            _shown = (SearchText, Filter);
        }

        IsTruncated = found.Count >= MaxShown;
        HasNoResults = !IsLibraryEmpty && tiles.Count == 0;

        // Checking files can take seconds (an offline network folder), so it runs off the UI thread, stops when a
        // newer refresh starts, and looks at each folder once: a folder that is gone means all its files are.
        _fileCheck?.Cancel();
        var check = _fileCheck = new CancellationTokenSource();
        (bool Missing, bool Editable)[] states;
        try
        {
            states = await Task.Run(() => CheckFiles(tiles, check.Token), check.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation)
        {
            return; // a newer refresh owns the grid now
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            tiles[i].SetFileState(states[i].Missing, states[i].Editable);
        }

        MissingCount = states.Count(s => s.Missing);
    }

    private static (bool Missing, bool Editable)[] CheckFiles(List<LibraryItemViewModel> tiles, CancellationToken cancel)
    {
        var folders = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string? path)
        {
            cancel.ThrowIfCancellationRequested();
            if (path is null || Path.GetDirectoryName(path) is not { } folder)
            {
                return false;
            }

            if (!folders.TryGetValue(folder, out var folderExists))
            {
                folders[folder] = folderExists = Directory.Exists(folder);
            }

            return folderExists && File.Exists(path);
        }

        return tiles.Select(t =>
        {
            var file = Exists(t.Item.FilePath);
            var project = Exists(t.Item.ProjectPath);
            return (Missing: t.Item.FilePath is null ? !project : !file && !project, Editable: project);
        }).ToArray();
    }

    /// <summary>Makes <paramref name="target"/> equal <paramref name="wanted"/> with as few changes as possible.</summary>
    private static void Sync(ObservableCollection<LibraryItemViewModel> target, List<LibraryItemViewModel> wanted)
    {
        var keep = new HashSet<LibraryItemViewModel>(wanted);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i]))
            {
                continue;
            }

            var at = target.IndexOf(wanted[i]);
            if (at >= 0)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, wanted[i]);
            }
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    partial void OnFilterChanged(LibraryFilter value) => Refresh();

    [RelayCommand]
    private Task CaptureRegion() => _commands.CaptureFromLibraryAsync(CaptureKind.Region);

    [RelayCommand]
    private void Record() => _commands.ToggleRecording();

    [RelayCommand]
    private void OpenSettings() => _commands.OpenSettings();

    [RelayCommand]
    private void Open(LibraryItemViewModel? item)
    {
        if (item is not null)
        {
            _actions.Open(item.Item);
        }
    }

    [RelayCommand]
    private void CopyImage(LibraryItemViewModel? item)
    {
        if (item is not null)
        {
            _actions.CopyImage(item.Item);
        }
    }

    [RelayCommand]
    private void CopyPath(LibraryItemViewModel? item)
    {
        if (item is not null)
        {
            _actions.CopyPath(item.Item);
        }
    }

    [RelayCommand]
    private void ShowInFolder(LibraryItemViewModel? item)
    {
        if (item is not null)
        {
            _actions.ShowInFolder(item.Item);
        }
    }

    [RelayCommand]
    private void RemoveMissing() => _actions.RemoveMissing(Items.Where(i => i.IsMissing).Select(i => i.Item));

    public bool Rename(LibraryItemViewModel item, string name) => _actions.Rename(item.Item, name);

    /// <param name="owner">The window that owns Windows' "delete permanently?" question, if one is needed.</param>
    public void Delete(IReadOnlyCollection<LibraryItemViewModel> items, nint owner) =>
        _actions.Delete(items.Select(i => i.Item).ToList(), owner);

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private string Describe(HotkeyAction action) =>
        _settings.Current.Hotkeys.TryGetValue(action, out var gesture) && !gesture.IsNone
            ? gesture.ToString()
            : "the tray menu";
}
