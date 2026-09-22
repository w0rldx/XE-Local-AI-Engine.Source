namespace XE_Local_AI_Engine.Desktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

internal sealed class DesktopCloseDialog : Window
{
    internal DesktopCloseDialog(bool trayAvailable, bool ownsEngine, bool settings, bool quitting = false)
    {
        Title = DesktopText.Settings;
        Width = 490;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var remember = new CheckBox { Content = DesktopText.Remember, IsChecked = true, IsVisible = !settings && !quitting };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = quitting ? DesktopText.QuitQuestion : DesktopText.CloseQuestion, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = ownsEngine ? DesktopText.Owned : DesktopText.Attached, TextWrapping = TextWrapping.Wrap });
        if (!trayAvailable && !quitting)
        {
            panel.Children.Add(new TextBlock { Text = DesktopText.NoTray, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(remember);
        if (!quitting)
        {
            AddChoice(panel, DesktopText.KeepInTray, DesktopCloseAction.Tray, trayAvailable, () => settings || remember.IsChecked == true);
        }
        AddChoice(panel, DesktopText.Quit, DesktopCloseAction.Quit, true, () => settings || remember.IsChecked == true);
        if (settings)
        {
            AddChoice(panel, DesktopText.Ask, DesktopCloseAction.Ask, true, () => true);
        }

        var cancel = new Button { Content = DesktopText.Cancel, HorizontalAlignment = HorizontalAlignment.Stretch, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(cancel);
        Content = panel;
    }

    private void AddChoice(StackPanel panel, string text, DesktopCloseAction action, bool enabled, Func<bool> remember)
    {
        var button = new Button { Content = text, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => Close(new DesktopCloseChoice { Action = action, Remember = remember() });
        panel.Children.Add(button);
    }
}
