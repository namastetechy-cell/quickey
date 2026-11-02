using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// Portal photo: pick the portal's rules (a preset, or custom size and KB limits), place the crop box
/// on the face or signature, and save one upload-ready JPEG. Shows the real resulting size live.
/// </summary>
public sealed class PortalWindow : ChoiceWindow<PortalChoice>
{
    // Remembered for the next photo and the next time: people upload to the same portal repeatedly.
    private static PortalPreset _last = PortalPreset.Passport;
    private static PortalPreset _lastCustom = PortalPreset.FromCm("Custom", "-portal", 3.5, 4.5, 300, 10, 50);
    private static bool _lastCm = true;

    private readonly string _file;
    private readonly CropStage _stage;
    private readonly StackPanel _custom = new() { Margin = new Thickness(24, 4, 0, 0) };
    private readonly TextBox _width = Field();
    private readonly TextBox _height = Field();
    private readonly ComboBox _unit = new() { ItemsSource = new[] { "cm", "px" }, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _dpi = Field();
    private readonly TextBox _minKb = Field();
    private readonly TextBox _maxKb = Field();
    private readonly TextBlock _result = DialogKit.Hint();
    private readonly DispatcherTimer _estimateDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private PortalPreset? _preset;
    private bool _isCustom;
    private int _estimateVersion;

    public PortalWindow(string file, ImagePreview preview, int index, int count)
    {
        _file = file;
        DialogKit.Style(this, count > 1 ? $"Portal photo: {Path.GetFileName(file)} ({index + 1} of {count})" : $"Portal photo: {Path.GetFileName(file)}");

        _stage = new CropStage(preview, maxWidth: 620, maxHeight: 520);
        _stage.SelectionChanged += (_, _) => QueueEstimate();

        var choices = new StackPanel();
        foreach (var preset in PortalPreset.BuiltIn)
            choices.Children.Add(Option(preset.Name, Describe(preset), preset == _last, () => Use(preset, custom: false)));
        var customChosen = !PortalPreset.BuiltIn.Contains(_last);
        choices.Children.Add(Option("Custom", "Any size and KB limits", customChosen, () => Use(CustomPreset(), custom: true)));

        _unit.SelectedItem = _lastCm ? "cm" : "px";
        _width.Text = _lastCm ? Cm(_lastCustom.WidthPx, _lastCustom.Dpi) : _lastCustom.WidthPx.ToString(CultureInfo.InvariantCulture);
        _height.Text = _lastCm ? Cm(_lastCustom.HeightPx, _lastCustom.Dpi) : _lastCustom.HeightPx.ToString(CultureInfo.InvariantCulture);
        _dpi.Text = _lastCustom.Dpi.ToString(CultureInfo.InvariantCulture);
        _minKb.Text = _lastCustom.MinKb.ToString(CultureInfo.InvariantCulture);
        _maxKb.Text = _lastCustom.MaxKb.ToString(CultureInfo.InvariantCulture);
        _custom.Children.Add(Row("Size", _width, new TextBlock { Text = "×", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, _height, _unit));
        _custom.Children.Add(Row("DPI", _dpi));
        _custom.Children.Add(Row("Size limit", _minKb, new TextBlock { Text = "to", Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }, _maxKb, new TextBlock { Text = "KB", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }));
        foreach (var box in new[] { _width, _height, _dpi, _minKb, _maxKb })
            box.TextChanged += (_, _) => { if (_isCustom) Use(CustomPreset(), custom: true); };
        _unit.SelectionChanged += (_, _) => { if (_isCustom) Use(CustomPreset(), custom: true); };

        var buttons = new List<Button> { DialogKit.Secondary(count > 1 ? "Cancel all" : "Cancel", (_, _) => Finish(null), isCancel: true) };
        if (count > 1)
            buttons.Add(DialogKit.Secondary("Skip", (_, _) => Finish(new PortalChoice(PortalAction.Skip))));
        buttons.Add(DialogKit.Primary("Save", (_, _) => Save()));

        var side = new StackPanel
        {
            Width = 320,
            Margin = new Thickness(20, 0, 0, 0),
            Children =
            {
                DialogKit.Heading("Portal rules"),
                choices,
                _custom,
                new Border { Height = 1, Background = DialogKit.Line, Margin = new Thickness(0, 16, 0, 10) },
                DialogKit.Hint("Move the box over the face (or the signature). It stays in the right shape."),
                new Border { Height = 8 },
                _result,
                DialogKit.ButtonRow([.. buttons]),
            },
        };

        var body = new DockPanel { Margin = new Thickness(20) };
        body.Children.Add(DialogKit.Docked(_stage, Dock.Left));
        body.Children.Add(side);
        Content = body;

        _estimateDelay.Tick += async (_, _) => await UpdateEstimate();
        Use(customChosen ? CustomPreset() : _last, customChosen);
    }

    internal PortalPreset? Preset => _preset;

    internal string ResultText => _result.Text;

    internal void SelectCustom(string width, string height, string unit, string dpi, string minKb, string maxKb)
    {
        _width.Text = width;
        _height.Text = height;
        _unit.SelectedItem = unit;
        _dpi.Text = dpi;
        _minKb.Text = minKb;
        _maxKb.Text = maxKb;
        Use(CustomPreset(), custom: true);
    }

    internal void SelectPreset(PortalPreset preset) => Use(preset, custom: false);

    private void Use(PortalPreset? preset, bool custom)
    {
        _isCustom = custom;
        _custom.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var problem = preset?.Problem() ?? "Enter numbers for the size, DPI and KB limits.";
        if (preset is null || preset.Problem() is not null)
        {
            _preset = null;
            _result.Text = problem;
            _result.Foreground = DialogKit.Warning;
            return;
        }
        var shapeChanged = _preset is null || Math.Abs(_preset.Ratio - preset.Ratio) > 1e-6;
        _preset = preset;
        if (shapeChanged)
            _stage.SetRatio(preset.Ratio);
        QueueEstimate();
    }

    /// <summary>The custom fields as a preset, or null while they aren't all numbers.</summary>
    private PortalPreset? CustomPreset()
    {
        static bool Number(TextBox box, out double value) =>
            double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

        if (!Number(_width, out var w) || !Number(_height, out var h) || !Number(_dpi, out var dpi) || !Number(_maxKb, out var max))
            return null;
        var min = double.TryParse(_minKb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var cm = (_unit.SelectedItem as string) == "cm";
        return cm
            ? PortalPreset.FromCm("Custom", "-portal", w, h, (int)dpi, (int)min, (int)max)
            : new PortalPreset("Custom", "-portal", (int)Math.Round(w), (int)Math.Round(h), (int)dpi, (int)min, (int)max);
    }

    private void QueueEstimate()
    {
        _estimateDelay.Stop();
        _estimateDelay.Start();
    }

    /// <summary>Makes the photo with the current box and rules in the background and shows the real result.</summary>
    internal async Task UpdateEstimate()
    {
        _estimateDelay.Stop();
        if (_preset is not { } preset)
            return;
        var version = ++_estimateVersion;
        var crop = _stage.Selection;
        _result.Foreground = DialogKit.Muted;
        _result.Text = "Working out the size…";
        try
        {
            var made = await Task.Run(() => PortalPhoto.Make(_file, crop, preset));
            if (version != _estimateVersion)
                return;
            var ok = !made.TooBig && !made.TooSmall;
            _result.Text = (ok ? "✓ " : "") + "Result: " + PortalPhoto.Describe(preset, made);
            _result.Foreground = ok ? DialogKit.Text : DialogKit.Warning;
        }
        catch (Exception ex)
        {
            if (version == _estimateVersion)
            {
                _result.Text = "Couldn't make the photo: " + ex.Message;
                _result.Foreground = DialogKit.Warning;
            }
        }
    }

    private void Save()
    {
        if (_preset is null)
            return;
        _last = _preset;
        if (_isCustom)
        {
            _lastCustom = _preset;
            _lastCm = (_unit.SelectedItem as string) == "cm";
        }
        Finish(new PortalChoice(PortalAction.Save, _preset, _stage.Selection));
    }

    private UIElement Option(string title, string detail, bool isChecked, Action onChecked)
    {
        var radio = new RadioButton { GroupName = "portal", IsChecked = isChecked, Foreground = DialogKit.Text, Margin = new Thickness(0, 4, 0, 4) };
        radio.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = detail, Foreground = DialogKit.Muted, FontSize = 12 },
            },
        };
        radio.Checked += (_, _) => onChecked();
        return radio;
    }

    private static string Describe(PortalPreset p) =>
        $"{p.WidthPx} × {p.HeightPx} px · {p.Dpi} DPI · " + (p.MinKb > 0 ? $"{p.MinKb}–{p.MaxKb} KB" : $"up to {p.MaxKb} KB");

    private static string Cm(int px, int dpi) =>
        (px / (double)dpi * PortalPreset.CmPerInch).ToString("0.##", CultureInfo.InvariantCulture);

    private static TextBox Field() => new() { Width = 56, Padding = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };

    private static UIElement Row(string label, params UIElement[] parts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 70, Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center });
        foreach (var part in parts)
            row.Children.Add(part);
        return row;
    }
}

public sealed class PortalPrompt : IPortalPrompt
{
    public async Task<PortalChoice> AskAsync(string file, int index, int count, CancellationToken cancellationToken)
    {
        var preview = await Task.Run(() => ImagePreview.Load(file, maxSide: 1200), cancellationToken);
        return await ChoiceWindow<PortalChoice>.AskAsync(() => new PortalWindow(file, preview, index, count))
            ?? new PortalChoice(PortalAction.CancelAll);
    }
}
