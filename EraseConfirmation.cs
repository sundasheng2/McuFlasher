using System.Windows;
using System.Windows.Controls;

namespace McuFlasher;

internal static class EraseConfirmation
{
    public static bool Show(Window owner, string message)
    {
        var dialog = new Window
        {
            Title = "确认擦除所选 Flash 页",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 450,
            MinWidth = 400,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontSize = 14
        };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 90, MinHeight = 30 };
        var confirm = new Button { Content = "擦除选中页", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }
}
