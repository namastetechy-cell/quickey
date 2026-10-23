using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Quickey.App.UI;

/// <summary>Shared look for Quickey's tool windows: dark surface, indigo accent, Segoe UI.</summary>
internal static class DialogKit
{
    public static readonly Brush Accent = Frozen(Color.FromRgb(0x63, 0x66, 0xF1));
    public static readonly Brush Surface = Frozen(Color.FromRgb(0x1C, 0x1B, 0x29));
    public static readonly Brush Panel = Frozen(Color.FromRgb(0x26, 0x25, 0x37));
    public static readonly Brush Line = Frozen(Color.FromRgb(0x33, 0x32, 0x46));
    public static readonly Brush Text = Frozen(Colors.White);
    public static readonly Brush Muted = Frozen(Color.FromRgb(0xA5, 0xA3, 0xC2));
    public static readonly Brush Warning = Frozen(Color.FromRgb(0xFB, 0x92, 0x3C));

    /// <summary>Applies the common window chrome. Topmost because Windows may refuse a tray app focus.</summary>
    public static void Style(Window window, string title)
    {
        window.Title = title;
        window.Background = Surface;
        window.Foreground = Text;
        window.FontFamily = new FontFamily("Segoe UI");
        window.FontSize = 13;
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Topmost = true;
        window.Loaded += (_, _) => window.Activate();
    }

    public static Button Primary(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, IsDefault = true, Padding = new Thickness(22, 6, 22, 6), Background = Accent, Foreground = Text, BorderThickness = new Thickness(0) };
        button.Click += click;
        return button;
    }

    public static Button Secondary(string text, RoutedEventHandler click, bool isCancel = false)
    {
        var button = new Button { Content = text, IsCancel = isCancel, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += click;
        return button;
    }

    /// <summary>A toggle-looking radio button; falls back to a plain radio button if the theme lacks the style.</summary>
    public static RadioButton Choice(Window owner, string text, string group, bool isChecked, Action onChecked)
    {
        var choice = new RadioButton
        {
            Content = text,
            GroupName = group,
            IsChecked = isChecked,
            Style = owner.TryFindResource(typeof(ToggleButton)) as Style,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(12, 5, 12, 5),
            MinWidth = 52,
        };
        choice.Checked += (_, _) => onChecked();
        return choice;
    }

    public static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    public static TextBlock Hint(string text = "") =>
        new() { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap };

    public static UIElement Docked(UIElement element, Dock dock)
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    public static BitmapImage Bitmap(byte[] encoded)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(encoded);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A tool window that answers with a value when closed: the choice, or null if cancelled.</summary>
public abstract class ChoiceWindow<T> : Window where T : class
{
    private readonly TaskCompletionSource<T?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected ChoiceWindow() => Closed += (_, _) => _result.TrySetResult(null);

    public Task<T?> Result => _result.Task;

    /// <summary>Completes with <paramref name="choice"/> (null = cancel) and closes.</summary>
    internal void Finish(T? choice)
    {
        _result.TrySetResult(choice);
        Close();
    }

    /// <summary>Shows the window for these files on the UI thread and waits for the answer.</summary>
    public static async Task<T?> AskAsync(Func<ChoiceWindow<T>> create)
    {
        var window = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var created = create();
            created.Show();
            return created;
        });
        return await window.Result;
    }
}
