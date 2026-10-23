using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// View and edit camera, date, author and location tags, or remove metadata. With several images,
/// fields they share show their value, differing fields stay blank, and only fields you change are written.
/// </summary>
public sealed class MetadataWindow : ChoiceWindow<MetadataChange>
{
    private readonly List<Field> _fields = [];
    private readonly TextBlock _error = new() { Foreground = DialogKit.Warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _overwrite = new() { Content = "Overwrite the original files", Foreground = DialogKit.Text, Margin = new Thickness(0, 12, 0, 0) };

    public MetadataWindow(IReadOnlyList<string> files)
    {
        DialogKit.Style(this, files.Count == 1 ? $"Metadata: {Path.GetFileName(files[0])}" : $"Metadata: {files.Count} images");
        var all = files.Select(MetadataEditor.Read).ToList();

        string? Common(Func<PhotoMetadata, string?> pick)
        {
            var values = all.Select(pick).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }
        bool Differs(Func<PhotoMetadata, string?> pick) => all.Select(pick).Distinct().Count() > 1;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        void Add(string label, Func<PhotoMetadata, string?> pick, Func<MetadataChange, string, MetadataChange> apply, string? hint = null)
        {
            var box = new TextBox { Text = Common(pick) ?? "", Padding = new Thickness(4), Margin = new Thickness(0, 3, 0, 3), ToolTip = hint };
            var field = new Field(label, box, apply);
            if (Differs(pick))
                box.ToolTip = "Differs between images. Type to set the same value on all.";
            box.TextChanged += (_, _) => field.Changed = true;
            _fields.Add(field);

            grid.RowDefinitions.Add(new RowDefinition());
            var row = grid.RowDefinitions.Count - 1;
            var text = new TextBlock { Text = label, Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }

        Add("Camera make", m => m.Make, (c, v) => c with { Make = v });
        Add("Camera model", m => m.Model, (c, v) => c with { Model = v });
        Add("Date taken", m => m.DateTaken, (c, v) => c with { DateTaken = v }, "e.g. 2024-05-01 14:30");
        Add("Author", m => m.Artist, (c, v) => c with { Artist = v });
        Add("Copyright", m => m.Copyright, (c, v) => c with { Copyright = v });
        Add("Description", m => m.Description, (c, v) => c with { Description = v });
        Add("Location", m => m.Location, (c, v) => c with { Location = v }, "latitude, longitude, e.g. 12.9716, 77.5946");

        var removeLocation = DialogKit.Secondary("Remove location", (_, _) => FieldNamed("Location").Box.Text = "");
        var removeAll = DialogKit.Secondary("Remove all metadata", (_, _) => RemoveAll());
        removeLocation.Margin = removeAll.Margin = new Thickness(0, 10, 8, 0);

        var withLocation = all.Count(m => m.Location is not null);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                DialogKit.Hint(files.Count == 1
                    ? (withLocation > 0 ? "This photo records where it was taken." : "No location recorded.")
                    : $"Blank fields differ between images and are left as they are unless you type a value. {withLocation} of {files.Count} record a location."),
                grid,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { removeLocation, removeAll } },
                _overwrite,
                DialogKit.Hint("Otherwise each is saved as a copy, e.g. photo-edited.jpg. JPEG photos are never re-compressed."),
                _error,
                DialogKit.ButtonRow(DialogKit.Secondary("Cancel", (_, _) => Finish(null), isCancel: true), DialogKit.Primary("Save", (_, _) => Save())),
            },
        };
    }

    internal Field FieldNamed(string label) => _fields.Single(f => f.Label == label);

    internal bool Overwrite
    {
        get => _overwrite.IsChecked == true;
        set => _overwrite.IsChecked = value;
    }

    /// <summary>The change described by the fields, or null with an explanation if a date or location doesn't parse.</summary>
    internal MetadataChange? Change()
    {
        var change = new MetadataChange { OverwriteOriginal = Overwrite };
        foreach (var field in _fields.Where(f => f.Changed))
            change = field.Apply(change, field.Box.Text.Trim());
        try
        {
            if (change.DateTaken is { Length: > 0 } date)
                MetadataEditor.ToExifDate(date);
            if (change.Location is { Length: > 0 } location)
                MetadataEditor.ParseLocation(location);
        }
        catch (FormatException ex)
        {
            _error.Text = ex.Message;
            return null;
        }
        return change;
    }

    private void Save()
    {
        if (Change() is { } change)
            Finish(change);
    }

    private void RemoveAll()
    {
        if (MessageBox.Show(this, "Remove all metadata (camera, date, location, author...)? The photo's rotation is kept.",
                "Remove all metadata", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Finish(new MetadataChange { RemoveAll = true, OverwriteOriginal = Overwrite });
    }

    internal sealed class Field(string label, TextBox box, Func<MetadataChange, string, MetadataChange> apply)
    {
        public string Label { get; } = label;
        public TextBox Box { get; } = box;
        public Func<MetadataChange, string, MetadataChange> Apply { get; } = apply;
        public bool Changed { get; set; }
    }
}

public sealed class MetadataPrompt : IMetadataPrompt
{
    public Task<MetadataChange?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        ChoiceWindow<MetadataChange>.AskAsync(() => new MetadataWindow(files));
}
