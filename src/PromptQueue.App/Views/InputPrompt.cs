using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PromptQueue.App.Views;

/// <summary>A tiny modal "type one line" dialog, styled to match the app.</summary>
public static class InputPrompt
{
    /// <summary>Shows the dialog; returns the trimmed text, or null if cancelled / empty.</summary>
    public static string? Ask(string title, string label, string initial = "")
    {
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(4, 3, 4, 3),
            MinWidth = 320,
        };
        box.SelectAll();

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 74, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 74 };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
        };

        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; win.DialogResult = true; };
        box.Loaded += (_, _) => box.Focus();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };

        win.ShowDialog();
        if (!accepted)
            return null;
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
