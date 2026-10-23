using MemoryStream = System.IO.MemoryStream;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// Pick a crop box on one image: freeform or a fixed shape (1:1, 9:16, 16:9...). Drag inside the
/// box to move it, drag an edge or corner to resize. Enter crops, Esc cancels the rest.
/// All geometry is in source pixels and done by <see cref="CropGeometry"/>; this class only draws.
/// </summary>
public sealed class CropWindow : Window
{
    private const double MaxDisplayWidth = 900;
    private const double MaxDisplayHeight = 560;
    private const double HandleSize = 10;
    private const double GrabTolerance = 10;

    // The last shape used is offered again for the next image, which is what a batch wants.
    private static AspectPreset _lastPreset = AspectPreset.Free;

    private static readonly Brush Accent = Frozen(Color.FromRgb(0x63, 0x66, 0xF1));
    private static readonly Brush Dark = Frozen(Color.FromRgb(0x1C, 0x1B, 0x29));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0xA5, 0xA3, 0xC2));

    private readonly ImagePreview _preview;
    private readonly double _scale; // display units per source pixel
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Path _shade = new() { Fill = Frozen(Color.FromArgb(0xA0, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Path _thirds = new() { Stroke = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly TextBlock _size = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    private readonly TaskCompletionSource<CropDecision> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AspectPreset _preset;
    private PixelRect _selection;
    private CropHandle _dragging = CropHandle.None;
    private Point _dragStart;
    private PixelRect _selectionAtDragStart;

    public CropWindow(ImagePreview preview, string fileName, int index, int count)
    {
        _preview = preview;
        _preset = _lastPreset;
        _scale = Math.Min(1, Math.Min(MaxDisplayWidth / preview.Width, MaxDisplayHeight / preview.Height)) * preview.Width / preview.SourceWidth;

        Title = count > 1 ? $"Crop {fileName} ({index + 1} of {count})" : $"Crop {fileName}";
        Background = Dark;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Quickey is a background app, so Windows may refuse it focus; topmost at least keeps the window in view.
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI");

        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                Docked(BuildPresets(), Dock.Top),
                Docked(BuildButtons(count), Dock.Bottom),
                BuildStage(),
            },
        };

        PreviewKeyDown += OnKey;
        Closed += (_, _) => _result.TrySetResult(new CropDecision(CropChoice.CancelAll));
        Loaded += (_, _) => Activate();
        ApplyPreset(_preset);
    }

    public Task<CropDecision> Result => _result.Task;

    internal PixelRect Selection => _selection;

    internal void SelectPreset(string label) => ApplyPreset(AspectPreset.Common.Single(p => p.Label == label));

    /// <summary>A drag of the given handle by an amount in display units, as the mouse would do.</summary>
    internal void SimulateDrag(CropHandle handle, double dx, double dy)
    {
        BeginDrag(handle, new Point(0, 0));
        ContinueDrag(new Point(dx, dy));
        _dragging = CropHandle.None;
    }

    internal void Finish(CropChoice choice)
    {
        _result.TrySetResult(new CropDecision(choice, _selection));
        Close();
    }

    private double? Ratio => _preset.RatioFor(_preview.SourceWidth, _preview.SourceHeight);

    private UIElement BuildPresets()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var preset in AspectPreset.Common)
        {
            var button = new RadioButton
            {
                Content = preset.Label,
                GroupName = "aspect",
                IsChecked = preset == _preset,
                // Toggle-button look for the shape choices; if the theme has no such style, plain radio buttons still work.
                Style = TryFindResource(typeof(ToggleButton)) as Style,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 52,
            };
            button.Checked += (_, _) => ApplyPreset(preset);
            bar.Children.Add(button);
        }
        return bar;
    }

    private UIElement BuildStage()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(_preview.Png);
        bitmap.EndInit();
        bitmap.Freeze();

        _canvas.Width = _preview.SourceWidth * _scale;
        _canvas.Height = _preview.SourceHeight * _scale;
        _canvas.Background = Brushes.Transparent;
        _canvas.Cursor = Cursors.Cross;
        _canvas.Children.Add(new Image { Source = bitmap, Width = _canvas.Width, Height = _canvas.Height, Stretch = Stretch.Fill });
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_thirds);
        _canvas.Children.Add(_frame);
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = Accent, IsHitTestVisible = false };
            _canvas.Children.Add(_handles[i]);
        }

        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            var at = e.GetPosition(_canvas);
            var handle = HandleAt(at);
            if (handle == CropHandle.None)
                return;
            BeginDrag(handle, at);
            _canvas.CaptureMouse();
            e.Handled = true;
        };
        _canvas.MouseMove += (_, e) =>
        {
            var at = e.GetPosition(_canvas);
            if (_dragging != CropHandle.None)
                ContinueDrag(at);
            else
                _canvas.Cursor = CursorFor(HandleAt(at));
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _dragging = CropHandle.None;
            _canvas.ReleaseMouseCapture();
        };

        return new Border { BorderBrush = Frozen(Color.FromRgb(0x33, 0x32, 0x46)), BorderThickness = new Thickness(1), Child = _canvas, HorizontalAlignment = HorizontalAlignment.Center };
    }

    private UIElement BuildButtons(int count)
    {
        var crop = new Button { Content = "Crop", IsDefault = true, Padding = new Thickness(22, 6, 22, 6), Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        crop.Click += (_, _) => Finish(CropChoice.Crop);
        var cancel = new Button { Content = count > 1 ? "Cancel all" : "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Finish(CropChoice.CancelAll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        if (count > 1)
        {
            var skip = new Button { Content = "Skip", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            skip.Click += (_, _) => Finish(CropChoice.Skip);
            buttons.Children.Add(skip);
        }
        buttons.Children.Add(crop);

        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        row.Children.Add(Docked(_size, Dock.Left));
        row.Children.Add(Docked(buttons, Dock.Right));
        return row;
    }

    private void ApplyPreset(AspectPreset preset)
    {
        _preset = _lastPreset = preset;
        _selection = CropGeometry.Initial(_preview.SourceWidth, _preview.SourceHeight, Ratio);
        Render();
    }

    private void BeginDrag(CropHandle handle, Point at)
    {
        _dragging = handle;
        _dragStart = at;
        _selectionAtDragStart = _selection;
    }

    private void ContinueDrag(Point at)
    {
        var dx = (at.X - _dragStart.X) / _scale;
        var dy = (at.Y - _dragStart.Y) / _scale;
        _selection = CropGeometry.Drag(_selectionAtDragStart, _dragging, dx, dy, Ratio, _preview.SourceWidth, _preview.SourceHeight);
        Render();
    }

    /// <summary>Which part of the box is under a point: an edge or corner, inside (move), or nothing.</summary>
    private CropHandle HandleAt(Point p)
    {
        var (left, top, right, bottom) = (_selection.X * _scale, _selection.Y * _scale, _selection.Right * _scale, _selection.Bottom * _scale);
        if (p.X < left - GrabTolerance || p.X > right + GrabTolerance || p.Y < top - GrabTolerance || p.Y > bottom + GrabTolerance)
            return CropHandle.None;

        var handle = CropHandle.None;
        if (Math.Abs(p.X - left) <= GrabTolerance) handle |= CropHandle.Left;
        else if (Math.Abs(p.X - right) <= GrabTolerance) handle |= CropHandle.Right;
        if (Math.Abs(p.Y - top) <= GrabTolerance) handle |= CropHandle.Top;
        else if (Math.Abs(p.Y - bottom) <= GrabTolerance) handle |= CropHandle.Bottom;
        return handle == CropHandle.None ? CropHandle.Move : handle;
    }

    private static Cursor CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.Move => Cursors.SizeAll,
        CropHandle.Left or CropHandle.Right => Cursors.SizeWE,
        CropHandle.Top or CropHandle.Bottom => Cursors.SizeNS,
        CropHandle.TopLeft or CropHandle.BottomRight => Cursors.SizeNWSE,
        CropHandle.TopRight or CropHandle.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    private void Render()
    {
        var box = new Rect(_selection.X * _scale, _selection.Y * _scale, _selection.Width * _scale, _selection.Height * _scale);

        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, _canvas.Width, _canvas.Height)), new RectangleGeometry(box));
        Canvas.SetLeft(_frame, box.X);
        Canvas.SetTop(_frame, box.Y);
        _frame.Width = box.Width;
        _frame.Height = box.Height;

        var thirds = new GeometryGroup();
        for (var i = 1; i <= 2; i++)
        {
            thirds.Children.Add(new LineGeometry(new Point(box.X + box.Width * i / 3, box.Y), new Point(box.X + box.Width * i / 3, box.Bottom)));
            thirds.Children.Add(new LineGeometry(new Point(box.X, box.Y + box.Height * i / 3), new Point(box.Right, box.Y + box.Height * i / 3)));
        }
        _thirds.Data = thirds;

        Point[] points =
        [
            box.TopLeft, new(box.X + box.Width / 2, box.Y), box.TopRight, new(box.Right, box.Y + box.Height / 2),
            box.BottomRight, new(box.X + box.Width / 2, box.Bottom), box.BottomLeft, new(box.X, box.Y + box.Height / 2),
        ];
        for (var i = 0; i < _handles.Length; i++)
        {
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }

        var (_, _, width, height) = _selection.ToPixels(_preview.SourceWidth, _preview.SourceHeight);
        _size.Text = $"{width} × {height} px";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(CropChoice.CancelAll);
            e.Handled = true;
        }
    }

    private static UIElement Docked(UIElement element, Dock dock)
    {
        DockPanel.SetDock(element, dock);
        return element;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
