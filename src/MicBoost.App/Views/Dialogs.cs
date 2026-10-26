using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MicBoost.App.Services;

namespace MicBoost.App.Views;

/// <summary>Small dark-themed dialogs built in code (confirm and text input).</summary>
public static class Dialogs
{
    public static bool Confirm(Window? owner, string title, string message, string okText, string cancelText = "Cancel")
    {
        var (window, body, buttons) = Create(owner, title);
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        bool result = false;
        var ok = new Button { Content = okText, Style = (Style)Application.Current.FindResource("AccentButton"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = cancelText, IsCancel = true };
        ok.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        cancel.Focus();
        window.ShowDialog();
        return result;
    }

    public static string? AskText(Window? owner, string title, string prompt, string initial)
    {
        var (window, body, buttons) = Create(owner, title);
        body.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = initial, MinWidth = 320, MaxLength = 40 };
        body.Children.Add(box);
        string? result = null;
        var ok = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("AccentButton"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); Keyboard.Focus(box); };
        window.ShowDialog();
        return result;
    }

    private static (Window, StackPanel, StackPanel) Create(Window? owner, string title)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(body);
        root.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsVisible: true } ? owner : null,
            ShowInTaskbar = owner is not { IsVisible: true },
            Background = (Brush)res["SurfaceBrush"],
            Foreground = (Brush)res["TextBrush"],
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 13,
        };
        window.SourceInitialized += (_, _) => NativeWindow.UseDarkTitleBar(window);
        return (window, body, buttons);
    }
}
