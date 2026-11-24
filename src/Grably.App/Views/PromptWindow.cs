using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Grably.App.Views;

/// <summary>A small "type a value" dialog (rename).</summary>
public sealed class PromptWindow : FluentWindow
{
    private readonly TextBox _input;

    private PromptWindow(string title, string label, string value)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        ShowInTaskbar = false;

        _input = new TextBox { Text = value, Margin = new Thickness(0, 8, 0, 16) };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
            }
        };

        var ok = new Button { Content = "OK", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };

        Content = new StackPanel
        {
            Children =
            {
                new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false },
                new StackPanel
                {
                    Margin = new Thickness(20, 4, 20, 20),
                    Children =
                    {
                        new System.Windows.Controls.TextBlock { Text = label },
                        _input,
                        new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                    },
                },
            },
        };

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>Asks for a value; returns null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string label, string value)
    {
        var prompt = new PromptWindow(title, label, value) { Owner = owner };
        return prompt.ShowDialog() == true ? prompt._input.Text : null;
    }
}
