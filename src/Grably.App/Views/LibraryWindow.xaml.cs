using Grably.App.Shell;
using Grably.App.ViewModels;
using Wpf.Ui.Controls;

namespace Grably.App.Views;

public partial class LibraryWindow : FluentWindow
{
    public LibraryWindow(LibraryViewModel viewModel, ThemeController theme)
    {
        DataContext = viewModel;
        InitializeComponent();
        theme.Attach(this);
    }
}
