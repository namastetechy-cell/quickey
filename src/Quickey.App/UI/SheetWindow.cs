using System.IO;
using System.Windows;
using System.Windows.Controls;
using Quickey.Core.Documents;

namespace Quickey.App.UI;

/// <summary>"Which sheets?" for a workbook with several. The current sheet is the default; Enter accepts it.</summary>
public sealed class SheetWindow : ChoiceWindow<SheetChoice>
{
    private readonly StackPanel _list = new() { Margin = new Thickness(24, 2, 0, 4) };
    private readonly List<CheckBox> _boxes = [];
    private readonly CheckBox _applyToRest = new() { Foreground = DialogKit.Text, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _remember = new() { Content = "Don't ask again", Foreground = DialogKit.Text, Margin = new Thickness(0, 6, 0, 0), ToolTip = "Change this later in Settings" };
    private SheetMode _mode = SheetMode.Current;

    public SheetWindow(string file, IReadOnlyList<string> sheets, string currentSheet, int index, int count)
    {
        DialogKit.Style(this, count > 1 ? $"Which sheets? {Path.GetFileName(file)} ({index + 1} of {count})" : $"Which sheets? {Path.GetFileName(file)}");

        foreach (var sheet in sheets)
        {
            var box = new CheckBox { Content = sheet, Tag = sheet, IsChecked = sheet == currentSheet, Foreground = DialogKit.Text, Margin = new Thickness(0, 2, 0, 2) };
            _boxes.Add(box);
            _list.Children.Add(box);
        }
        _list.IsEnabled = false;

        var options = new StackPanel();
        options.Children.Add(Option($"Current sheet: {currentSheet}", SheetMode.Current, isChecked: true));
        options.Children.Add(Option($"All {sheets.Count} sheets", SheetMode.All));
        options.Children.Add(Option("Choose sheets:", SheetMode.Chosen));
        options.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var rest = count - index - 1;
        _applyToRest.Content = $"Do the same for the other {rest} workbook{(rest == 1 ? "" : "s")}";
        _applyToRest.Visibility = rest > 0 ? Visibility.Visible : Visibility.Collapsed;

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            MinWidth = 360,
            Children =
            {
                DialogKit.Hint("This workbook has several sheets. The current sheet is the one that was open when it was last saved."),
                new Border { Height = 10 },
                options,
                _applyToRest,
                _remember,
                DialogKit.ButtonRow(DialogKit.Secondary("Cancel", (_, _) => Finish(null), isCancel: true), DialogKit.Primary("Convert", (_, _) => Finish(Choice()))),
            },
        };
    }

    private RadioButton Option(string text, SheetMode mode, bool isChecked = false)
    {
        var option = new RadioButton { Content = text, GroupName = "sheets", IsChecked = isChecked, Foreground = DialogKit.Text, Margin = new Thickness(0, 4, 0, 4) };
        option.Checked += (_, _) => SetMode(mode);
        return option;
    }

    internal void SetMode(SheetMode mode)
    {
        _mode = mode;
        _list.IsEnabled = mode == SheetMode.Chosen;
        // Hand-picked sheet names don't carry over to other workbooks or future conversions.
        _applyToRest.IsEnabled = _remember.IsEnabled = mode != SheetMode.Chosen;
        if (mode == SheetMode.Chosen)
            _applyToRest.IsChecked = _remember.IsChecked = false;
    }

    internal void Check(string sheet, bool isChecked) => _boxes.Single(b => (string)b.Tag == sheet).IsChecked = isChecked;

    internal bool ApplyToRest { set => _applyToRest.IsChecked = value; }

    internal bool Remember { set => _remember.IsChecked = value; }

    internal SheetChoice Choice() => new(
        _mode,
        _mode == SheetMode.Chosen ? _boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList() : null,
        ApplyToRest: _applyToRest.IsChecked == true,
        Remember: _remember.IsChecked == true);
}

public sealed class SheetPrompt : ISheetPrompt
{
    public Task<SheetChoice?> AskAsync(string file, IReadOnlyList<string> sheets, string currentSheet, int index, int count, CancellationToken cancellationToken) =>
        ChoiceWindow<SheetChoice>.AskAsync(() => new SheetWindow(file, sheets, currentSheet, index, count));
}
