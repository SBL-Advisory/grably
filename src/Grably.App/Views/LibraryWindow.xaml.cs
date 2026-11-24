using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Grably.App.Shell;
using Grably.App.ViewModels;
using Wpf.Ui.Controls;

namespace Grably.App.Views;

public partial class LibraryWindow : FluentWindow
{
    private static readonly (LibraryFilter Filter, string Label)[] Filters =
    [
        (LibraryFilter.All, "All"),
        (LibraryFilter.Screenshots, "Screenshots"),
        (LibraryFilter.Recordings, "Recordings"),
        (LibraryFilter.Gifs, "GIFs"),
    ];

    private readonly LibraryViewModel _viewModel;
    private readonly Dictionary<LibraryFilter, ToggleButton> _filterButtons = [];
    private Point? _dragStart;

    public LibraryWindow(LibraryViewModel viewModel, ThemeController theme)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        theme.Attach(this);

        foreach (var (filter, label) in Filters)
        {
            var button = new ToggleButton { Content = label, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(12, 5, 12, 6) };
            button.Click += (_, _) => _viewModel.Filter = filter;
            _filterButtons[filter] = button;
            FilterButtons.Children.Add(button);
        }

        UpdateFilterButtons();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.Filter))
            {
                UpdateFilterButtons();
            }
        };
        Activated += (_, _) => _viewModel.Refresh(); // files may have changed outside Grably
        Closed += (_, _) => _viewModel.Detach();
    }

    private IReadOnlyList<LibraryItemViewModel> Selected => ItemsList.SelectedItems.Cast<LibraryItemViewModel>().ToList();

    private LibraryItemViewModel? Current => ItemsList.SelectedItem as LibraryItemViewModel;

    private void UpdateFilterButtons()
    {
        foreach (var (filter, button) in _filterButtons)
        {
            button.IsChecked = filter == _viewModel.Filter;
        }
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e) is { } item)
        {
            _viewModel.OpenCommand.Execute(item);
        }
    }

    private void ItemsList_KeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter when Current is { } item:
                _viewModel.OpenCommand.Execute(item);
                break;
            case Key.Delete when Selected.Count > 0:
                _viewModel.Delete(Selected.ToList(), new WindowInteropHelper(this).Handle);
                break;
            case Key.F2 when Current is { } item:
                Rename(item);
                break;
            case Key.C when ctrl && Current is { } item:
                _viewModel.CopyImageCommand.Execute(item);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _viewModel.OpenCommand.Execute(Current);

    private void CopyImage_Click(object sender, RoutedEventArgs e) => _viewModel.CopyImageCommand.Execute(Current);

    private void CopyPath_Click(object sender, RoutedEventArgs e) => _viewModel.CopyPathCommand.Execute(Current);

    private void ShowInFolder_Click(object sender, RoutedEventArgs e) => _viewModel.ShowInFolderCommand.Execute(Current);

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item)
        {
            Rename(item);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected.Count > 0)
        {
            _viewModel.Delete(Selected.ToList(), new WindowInteropHelper(this).Handle);
        }
    }

    private void Rename(LibraryItemViewModel item)
    {
        var name = PromptWindow.Ask(this, "Rename", "New name", item.Title);
        if (name is not null)
        {
            _viewModel.Rename(item, name);
        }
    }

    // Dragging a tile out drops its file into the other app (chat, email, documents).
    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = ItemUnder(e) is null ? null : e.GetPosition(ItemsList);

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(ItemsList) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        var files = Selected.Select(i => i.Item.FilePath).OfType<string>().Where(File.Exists).ToArray();
        if (files.Length > 0)
        {
            var data = new DataObject();
            data.SetFileDropList([.. files]);
            DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy);
        }
    }

    private static LibraryItemViewModel? ItemUnder(MouseEventArgs e) =>
        (e.OriginalSource as DependencyObject) is { } source
            && System.Windows.Media.VisualTreeHelper.GetParent(source) is not null
            && FindItem(source) is { } item
            ? item
            : null;

    private static LibraryItemViewModel? FindItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: LibraryItemViewModel item })
            {
                return item;
            }

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
