using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MagickImage = ImageMagick.MagickImage;
using Quickey.Core.Images;
using Image = System.Windows.Controls.Image;

namespace Quickey.App.UI;

/// <summary>
/// Adjust exposure, contrast, colour, detail and effects with a live preview. The preview is a
/// downscaled copy run through exactly the same pipeline as the saved file.
/// </summary>
public sealed class EditWindow : ChoiceWindow<PhotoAdjustments>
{
    private const int PreviewSide = 1000;

    private static readonly (PhotoEffect Effect, string Label)[] Effects =
    [
        (PhotoEffect.None, "None"), (PhotoEffect.BlackAndWhite, "B&W"), (PhotoEffect.Sepia, "Sepia"), (PhotoEffect.Vintage, "Vintage"),
        (PhotoEffect.Vivid, "Vivid"), (PhotoEffect.Cool, "Cool"), (PhotoEffect.Warm, "Warm"), (PhotoEffect.Fade, "Fade"),
    ];

    private readonly MagickImage _source;
    private readonly byte[] _originalPng;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, Width = 720, Height = 520 };
    private readonly DispatcherTimer _renderDelay = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly WrapPanel _effects = new();
    private PhotoAdjustments _adjustments = new();
    private int _renderVersion;

    public EditWindow(IReadOnlyList<string> files, ImagePreview preview)
    {
        DialogKit.Style(this, files.Count == 1 ? $"Edit {Path.GetFileName(files[0])}" : $"Edit {files.Count} photos");
        _originalPng = preview.Png;
        _source = new MagickImage(preview.Png);
        _image.Source = DialogKit.Bitmap(preview.Png);

        var controls = new StackPanel { Width = 280, Margin = new Thickness(20, 0, 0, 0) };
        controls.Children.Add(DialogKit.Heading("Light"));
        AddSlider(controls, "Exposure", -100, 100, (a, v) => a with { Exposure = v });
        AddSlider(controls, "Contrast", -100, 100, (a, v) => a with { Contrast = v });
        controls.Children.Add(DialogKit.Heading("Colour"));
        AddSlider(controls, "Saturation", -100, 100, (a, v) => a with { Saturation = v });
        AddSlider(controls, "Warmth", -100, 100, (a, v) => a with { Warmth = v });
        controls.Children.Add(DialogKit.Heading("Detail"));
        AddSlider(controls, "Sharpness", 0, 100, (a, v) => a with { Sharpness = v });
        AddSlider(controls, "Vignette", 0, 100, (a, v) => a with { Vignette = v });

        controls.Children.Add(DialogKit.Heading("Effects"));
        foreach (var (effect, label) in Effects)
            _effects.Children.Add(DialogKit.Choice(this, label, "effect", effect == PhotoEffect.None, () => Update(_adjustments with { Effect = effect })));
        controls.Children.Add(_effects);

        controls.Children.Add(DialogKit.Heading("Orientation"));
        var orientation = new WrapPanel();
        orientation.Children.Add(DialogKit.Secondary("⟲ Left", (_, _) => Update(_adjustments with { Rotation = (_adjustments.Rotation + 270) % 360 })));
        orientation.Children.Add(DialogKit.Secondary("⟳ Right", (_, _) => Update(_adjustments with { Rotation = (_adjustments.Rotation + 90) % 360 })));
        orientation.Children.Add(DialogKit.Secondary("Flip", (_, _) => Update(_adjustments with { FlipHorizontal = !_adjustments.FlipHorizontal })));
        controls.Children.Add(orientation);

        var compare = DialogKit.Secondary("Hold to compare", (_, _) => { });
        compare.PreviewMouseLeftButtonDown += (_, _) => _image.Source = DialogKit.Bitmap(_originalPng);
        compare.PreviewMouseLeftButtonUp += (_, _) => Render();
        compare.MouseLeave += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Pressed) Render(); };

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        footer.Children.Add(DialogKit.Docked(compare, Dock.Left));
        footer.Children.Add(DialogKit.Docked(DialogKit.Secondary("Reset", (_, _) => Reset()), Dock.Left));
        if (files.Count > 1)
            footer.Children.Add(DialogKit.Docked(new TextBlock { Text = $"Applies to all {files.Count} photos", Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center }, Dock.Left));
        var save = DialogKit.ButtonRow(DialogKit.Secondary("Cancel", (_, _) => Finish(null), isCancel: true), DialogKit.Primary("Save", (_, _) => Finish(_adjustments)));
        save.Margin = new Thickness(0);
        footer.Children.Add(DialogKit.Docked(save, Dock.Right));

        var body = new DockPanel();
        body.Children.Add(DialogKit.Docked(new Border { Background = DialogKit.Panel, Child = _image }, Dock.Left));
        body.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 });

        Content = new DockPanel { Margin = new Thickness(20), Children = { DialogKit.Docked(footer, Dock.Bottom), body } };

        _renderDelay.Tick += (_, _) => Render();
        Closed += (_, _) => _source.Dispose();
    }

    internal PhotoAdjustments Adjustments => _adjustments;

    internal void SetSlider(string name, int value) => _sliders[name].Value = value;

    internal void SetEffect(PhotoEffect effect) => Update(_adjustments with { Effect = effect });

    /// <summary>Renders the preview now and waits for it (tests use this; the UI renders on a short delay).</summary>
    internal Task RenderNowAsync() => RenderAsync();

    private void AddSlider(Panel panel, string name, int min, int max, Func<PhotoAdjustments, int, PhotoAdjustments> apply)
    {
        var value = new TextBlock { Foreground = DialogKit.Muted, Width = 36, TextAlignment = TextAlignment.Right, Text = "0" };
        var slider = new Slider { Minimum = min, Maximum = max, Value = 0, IsSnapToTickEnabled = true, TickFrequency = 1, ToolTip = "Double-click to reset" };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = ((int)e.NewValue).ToString("+0;-0;0");
            Update(apply(_adjustments, (int)e.NewValue));
        };
        slider.MouseDoubleClick += (_, _) => slider.Value = 0;
        _sliders[name] = slider;

        var header = new DockPanel();
        header.Children.Add(DialogKit.Docked(value, Dock.Right));
        header.Children.Add(new TextBlock { Text = name });
        panel.Children.Add(header);
        panel.Children.Add(slider);
    }

    private void Update(PhotoAdjustments adjustments)
    {
        _adjustments = adjustments;
        _renderDelay.Stop();
        _renderDelay.Start();
    }

    private void Reset()
    {
        foreach (var slider in _sliders.Values)
            slider.Value = 0;
        if (_effects.Children[0] is RadioButton none)
            none.IsChecked = true;
        Update(new PhotoAdjustments());
    }

    private async void Render() => await RenderAsync();

    private async Task RenderAsync()
    {
        _renderDelay.Stop();
        var version = ++_renderVersion;
        var adjustments = _adjustments;
        var png = await Task.Run(() => PhotoEditor.RenderPreview(_source, adjustments));
        if (version == _renderVersion)
            _image.Source = DialogKit.Bitmap(png);
    }
}

public sealed class EditPrompt : IPhotoEditPrompt
{
    public async Task<PhotoAdjustments?> AskAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var preview = await Task.Run(() => ImagePreview.Load(files[0], maxSide: 1000), cancellationToken);
        return await ChoiceWindow<PhotoAdjustments>.AskAsync(() => new EditWindow(files, preview));
    }
}
