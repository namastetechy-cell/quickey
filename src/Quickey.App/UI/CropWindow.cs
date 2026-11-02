using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// Pick a crop box on one image: freeform or a fixed shape (1:1, 9:16, 16:9...). Enter crops,
/// Esc cancels the rest. The box itself is a <see cref="CropStage"/>.
/// </summary>
public sealed class CropWindow : Window
{
    // The last shape used is offered again for the next image, which is what a batch wants.
    private static AspectPreset _lastPreset = AspectPreset.Free;

    private readonly ImagePreview _preview;
    private readonly CropStage _stage;
    private readonly TextBlock _size = new() { Foreground = DialogKit.Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly TaskCompletionSource<CropDecision> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AspectPreset _preset;

    public CropWindow(ImagePreview preview, string fileName, int index, int count)
    {
        _preview = preview;
        _preset = _lastPreset;
        DialogKit.Style(this, count > 1 ? $"Crop {fileName} ({index + 1} of {count})" : $"Crop {fileName}");

        _stage = new CropStage(preview, maxWidth: 900, maxHeight: 560);
        _stage.SelectionChanged += (_, _) =>
        {
            var (_, _, width, height) = _stage.Selection.ToPixels(_preview.SourceWidth, _preview.SourceHeight);
            _size.Text = $"{width} × {height} px";
        };

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                DialogKit.Docked(BuildPresets(), Dock.Top),
                DialogKit.Docked(BuildButtons(count), Dock.Bottom),
                _stage,
            },
        };

        PreviewKeyDown += OnKey;
        Closed += (_, _) => _result.TrySetResult(new CropDecision(CropChoice.CancelAll));
        ApplyPreset(_preset);
    }

    public Task<CropDecision> Result => _result.Task;

    internal PixelRect Selection => _stage.Selection;

    internal void SelectPreset(string label) => ApplyPreset(AspectPreset.Common.Single(p => p.Label == label));

    internal void SimulateDrag(CropHandle handle, double dx, double dy) => _stage.SimulateDrag(handle, dx, dy);

    internal void Finish(CropChoice choice)
    {
        _result.TrySetResult(new CropDecision(choice, _stage.Selection));
        Close();
    }

    private UIElement BuildPresets()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var preset in AspectPreset.Common)
            bar.Children.Add(DialogKit.Choice(this, preset.Label, "aspect", preset == _preset, () => ApplyPreset(preset)));
        return bar;
    }

    private UIElement BuildButtons(int count)
    {
        var buttons = new List<Button> { DialogKit.Secondary(count > 1 ? "Cancel all" : "Cancel", (_, _) => Finish(CropChoice.CancelAll)) };
        if (count > 1)
            buttons.Add(DialogKit.Secondary("Skip", (_, _) => Finish(CropChoice.Skip)));
        buttons.Add(DialogKit.Primary("Crop", (_, _) => Finish(CropChoice.Crop)));
        var row = DialogKit.ButtonRow([.. buttons]);
        row.Margin = new Thickness(0);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        footer.Children.Add(DialogKit.Docked(_size, Dock.Left));
        footer.Children.Add(DialogKit.Docked(row, Dock.Right));
        return footer;
    }

    private void ApplyPreset(AspectPreset preset)
    {
        _preset = _lastPreset = preset;
        _stage.SetRatio(preset.RatioFor(_preview.SourceWidth, _preview.SourceHeight));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(CropChoice.CancelAll);
            e.Handled = true;
        }
    }
}
