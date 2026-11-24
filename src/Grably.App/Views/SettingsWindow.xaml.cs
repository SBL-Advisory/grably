using System.Diagnostics;
using System.Windows;
using Grably.App.Shell;
using Grably.App.ViewModels;
using Wpf.Ui.Controls;

namespace Grably.App.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsViewModel _viewModel;
    private int _recording;

    private readonly UpdateService _updates;

    public SettingsWindow(SettingsViewModel viewModel, ThemeController theme, UpdateService updates)
    {
        _viewModel = viewModel;
        _updates = updates;
        DataContext = viewModel;
        InitializeComponent();
        theme.Attach(this);
        Closed += (_, _) =>
        {
            if (_recording > 0)
            {
                _recording = 0;
                _viewModel.ResumeHotkeys();
            }
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save())
        {
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // While a shortcut box has focus, Grably's own hotkeys are released, so pressing them is recorded, not run.
    private void HotkeyBox_RecordingChanged(object? sender, bool recording)
    {
        if (recording && _recording++ == 0)
        {
            _viewModel.SuspendHotkeys();
        }
        else if (!recording && _recording > 0 && --_recording == 0)
        {
            _viewModel.ResumeHotkeys();
        }
    }

    private void ChangeScreenshotsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_viewModel.ScreenshotsFolder) is { } folder)
        {
            _viewModel.SetScreenshotsFolder(folder);
        }
    }

    private void ResetScreenshotsFolder_Click(object sender, RoutedEventArgs e) => _viewModel.SetScreenshotsFolder(null);

    private void ChangeVideosFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(_viewModel.VideosFolder) is { } folder)
        {
            _viewModel.SetVideosFolder(folder);
        }
    }

    private void ResetVideosFolder_Click(object sender, RoutedEventArgs e) => _viewModel.SetVideosFolder(null);

    private void OpenKeyboardSettings_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true });

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) => _ = _updates.CheckAsync(userAsked: true);

    /// <summary>The notices installed next to Grably.exe, or the ones on GitHub when running from a build folder.</summary>
    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        var notices = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        var target = System.IO.File.Exists(notices) ? notices : $"{UpdateService.Repository}/blob/main/THIRD-PARTY-NOTICES.md";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No app is set to open .md files: show it in Notepad, which every Windows has.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{notices}\"") { UseShellExecute = true });
        }
    }

    private void Credit_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(Core.AppInfo.CreditUrl) { UseShellExecute = true });

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_viewModel.DataFolder}\"") { UseShellExecute = true });

    private string? PickFolder(string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder", InitialDirectory = current };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
