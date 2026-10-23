using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// Compress options: a preset, a target size per image, or a percentage reduction, with optional
/// resize and format change. Shows a live estimate for the first image.
/// </summary>
public sealed class CompressWindow : ChoiceWindow<CompressOptions>
{
    private static readonly (string Label, int? Side)[] Sizes =
        [("Don't resize", null), ("Longest side 3840 px", 3840), ("2560 px", 2560), ("1920 px", 1920), ("1280 px", 1280), ("1024 px", 1024), ("800 px", 800)];
    private static readonly (string Label, ImageTarget? Target)[] Formats =
        [("Keep format", null), ("JPG", ImageTarget.Jpg), ("WebP", ImageTarget.WebP), ("AVIF", ImageTarget.Avif), ("PNG", ImageTarget.Png)];
    private static readonly (CompressPreset Preset, string Label, string Hint)[] Presets =
    [
        (CompressPreset.HighQuality, "High quality", "Barely visible difference"),
        (CompressPreset.Balanced, "Balanced", "Good for sharing and the web"),
        (CompressPreset.Small, "Small", "Noticeably softer, much smaller"),
        (CompressPreset.Smallest, "Smallest", "For previews and attachments"),
    ];

    // Remembered between uses: people usually compress the same way every time.
    private static CompressOptions _last = new();

    private readonly IReadOnlyList<string> _files;
    private readonly DispatcherTimer _estimateDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TextBlock _estimate = DialogKit.Hint();
    private readonly StackPanel _presetPanel = new();
    private readonly StackPanel _targetPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _percentPanel = new();
    private readonly TextBox _targetValue = new() { Width = 80, Padding = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _targetUnit = new() { Width = 60, Margin = new Thickness(6, 0, 0, 0), ItemsSource = new[] { "KB", "MB" } };
    private readonly Slider _percent = new() { Minimum = 10, Maximum = 90, Width = 260, IsSnapToTickEnabled = true, TickFrequency = 5 };
    private readonly TextBlock _percentLabel = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _size = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _format = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _keepMetadata = new() { Content = "Keep date, camera and location", Foreground = DialogKit.Text, Margin = new Thickness(0, 12, 0, 0) };
    private CompressMode _mode;
    private CompressPreset _preset;
    private int _estimateVersion;

    public CompressWindow(IReadOnlyList<string> files)
    {
        _files = files;
        var total = files.Sum(f => new FileInfo(f).Length);
        DialogKit.Style(this, files.Count == 1 ? $"Compress {Path.GetFileName(files[0])}" : $"Compress {files.Count} images");

        _mode = _last.Mode;
        _preset = _last.Preset;
        _targetValue.Text = _last.TargetBytes >= 1024 * 1024 ? (_last.TargetBytes / 1024d / 1024).ToString("0.#") : (_last.TargetBytes / 1024).ToString();
        _targetUnit.SelectedIndex = _last.TargetBytes >= 1024 * 1024 ? 1 : 0;
        _percent.Value = _last.ReducePercent;
        _size.ItemsSource = Sizes.Select(s => s.Label).ToList();
        _size.SelectedIndex = Math.Max(0, Array.FindIndex(Sizes, s => s.Side == _last.MaxSide));
        _format.ItemsSource = Formats.Select(f => f.Label).ToList();
        _format.SelectedIndex = Math.Max(0, Array.FindIndex(Formats, f => f.Target == _last.Format));
        _keepMetadata.IsChecked = _last.KeepMetadata;

        var modes = new WrapPanel();
        modes.Children.Add(DialogKit.Choice(this, "Preset", "mode", _mode == CompressMode.Preset, () => SetMode(CompressMode.Preset)));
        modes.Children.Add(DialogKit.Choice(this, "Target size", "mode", _mode == CompressMode.TargetSize, () => SetMode(CompressMode.TargetSize)));
        modes.Children.Add(DialogKit.Choice(this, "Reduce by %", "mode", _mode == CompressMode.Percent, () => SetMode(CompressMode.Percent)));

        foreach (var (preset, label, hint) in Presets)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(DialogKit.Docked(DialogKit.Choice(this, label, "preset", preset == _preset, () => { _preset = preset; QueueEstimate(); }), Dock.Left));
            row.Children.Add(new TextBlock { Text = hint, Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6) });
            _presetPanel.Children.Add(row);
        }
        _targetPanel.Children.Add(_targetValue);
        _targetPanel.Children.Add(_targetUnit);
        _targetPanel.Children.Add(new TextBlock { Text = "per image, or smaller", Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        _percentPanel.Children.Add(_percent);
        _percentPanel.Children.Add(_percentLabel);

        _targetValue.TextChanged += (_, _) => QueueEstimate();
        _targetUnit.SelectionChanged += (_, _) => QueueEstimate();
        _percent.ValueChanged += (_, _) => { _percentLabel.Text = $"{_percent.Value:0}% smaller"; QueueEstimate(); };
        _size.SelectionChanged += (_, _) => QueueEstimate();
        _format.SelectionChanged += (_, _) => QueueEstimate();
        _keepMetadata.Checked += (_, _) => QueueEstimate();
        _keepMetadata.Unchecked += (_, _) => QueueEstimate();
        _estimateDelay.Tick += async (_, _) => await UpdateEstimate();
        _percentLabel.Text = $"{_percent.Value:0}% smaller";

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Width = 460,
            Children =
            {
                DialogKit.Hint(files.Count == 1 ? $"Original: {ImageCompressor.FormatBytes(total)}" : $"{files.Count} images, {ImageCompressor.FormatBytes(total)} in total"),
                DialogKit.Heading("How small"),
                modes,
                _presetPanel,
                _targetPanel,
                _percentPanel,
                DialogKit.Heading("Resize while compressing"),
                _size,
                DialogKit.Heading("Format"),
                _format,
                _keepMetadata,
                new Border { Height = 1, Background = DialogKit.Line, Margin = new Thickness(0, 16, 0, 10) },
                _estimate,
                DialogKit.ButtonRow(DialogKit.Secondary("Cancel", (_, _) => Finish(null), isCancel: true), DialogKit.Primary("Compress", (_, _) => Confirm())),
            },
        };

        SetMode(_mode);
    }

    internal void SetMode(CompressMode mode)
    {
        _mode = mode;
        _presetPanel.Visibility = mode == CompressMode.Preset ? Visibility.Visible : Visibility.Collapsed;
        _targetPanel.Visibility = mode == CompressMode.TargetSize ? Visibility.Visible : Visibility.Collapsed;
        _percentPanel.Visibility = mode == CompressMode.Percent ? Visibility.Visible : Visibility.Collapsed;
        QueueEstimate();
    }

    internal void SetTarget(string value, string unit)
    {
        _targetValue.Text = value;
        _targetUnit.SelectedItem = unit;
    }

    internal string EstimateText => _estimate.Text;

    /// <summary>The options as currently set, or null with a message if the target size isn't a number.</summary>
    internal CompressOptions? Options()
    {
        long targetBytes = 0;
        if (_mode == CompressMode.TargetSize)
        {
            if (!double.TryParse(_targetValue.Text, out var value) || value <= 0)
                return null;
            targetBytes = (long)(value * (_targetUnit.SelectedIndex == 1 ? 1024 * 1024 : 1024));
        }
        return new CompressOptions
        {
            Mode = _mode,
            Preset = _preset,
            TargetBytes = targetBytes > 0 ? targetBytes : _last.TargetBytes,
            ReducePercent = (int)_percent.Value,
            MaxSide = Sizes[Math.Max(0, _size.SelectedIndex)].Side,
            Format = Formats[Math.Max(0, _format.SelectedIndex)].Target,
            KeepMetadata = _keepMetadata.IsChecked == true,
        };
    }

    private void Confirm()
    {
        if (Options() is not { } options)
        {
            _estimate.Text = "Enter a target size, e.g. 500 KB.";
            _estimate.Foreground = DialogKit.Warning;
            return;
        }
        _last = options;
        Finish(options);
    }

    private void QueueEstimate()
    {
        _estimateDelay.Stop();
        _estimateDelay.Start();
    }

    /// <summary>Compresses the first image in the background with the current settings and reports the size.</summary>
    internal async Task UpdateEstimate()
    {
        _estimateDelay.Stop();
        var version = ++_estimateVersion;
        _estimate.Foreground = DialogKit.Muted;
        if (Options() is not { } options)
        {
            _estimate.Text = "Enter a target size, e.g. 500 KB.";
            return;
        }
        _estimate.Text = "Estimating…";
        try
        {
            var result = await Task.Run(() => ImageCompressor.Compress(_files[0], options));
            if (version != _estimateVersion)
                return; // settings changed while this ran
            var saved = 100 - result.Data.Length * 100 / Math.Max(1, result.OriginalBytes);
            var who = _files.Count == 1 ? "" : " (first image)";
            _estimate.Text = $"Estimate{who}: {ImageCompressor.FormatBytes(result.OriginalBytes)} → {ImageCompressor.FormatBytes(result.Data.Length)}"
                + (saved > 0 ? $", {saved}% smaller" : ", no smaller")
                + $" · {result.Width} × {result.Height}"
                + (result.MetTarget ? "" : ". Can't reach the target; this is as small as it gets.");
            _estimate.Foreground = result.MetTarget && saved > 0 ? DialogKit.Muted : DialogKit.Warning;
        }
        catch (Exception ex)
        {
            if (version == _estimateVersion)
                _estimate.Text = "Couldn't estimate: " + ex.Message;
        }
    }
}

public sealed class CompressPrompt : ICompressPrompt
{
    public Task<CompressOptions?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) =>
        ChoiceWindow<CompressOptions>.AskAsync(() => new CompressWindow(files));
}
