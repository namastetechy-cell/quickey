using MemoryStream = System.IO.MemoryStream;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Quickey.Core.Images;

namespace Quickey.App.UI;

/// <summary>
/// An image with a crop box on top: drag inside to move it, drag an edge or corner to resize, the
/// chosen ratio stays locked. Shared by the Crop and Portal photo windows. All geometry is in source
/// pixels and done by <see cref="CropGeometry"/>; this class only draws and forwards the mouse.
/// </summary>
public sealed class CropStage : Border
{
    private const double HandleSize = 10;
    private const double GrabTolerance = 10;

    private readonly ImagePreview _preview;
    private readonly double _scale; // display units per source pixel
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Path _shade = new() { Fill = DialogKit.Frozen(Color.FromArgb(0xA0, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly Path _thirds = new() { Stroke = DialogKit.Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Rectangle[] _handles = new Rectangle[8];

    private double? _ratio;
    private PixelRect _selection;
    private CropHandle _dragging = CropHandle.None;
    private Point _dragStart;
    private PixelRect _selectionAtDragStart;

    public CropStage(ImagePreview preview, double maxWidth, double maxHeight)
    {
        _preview = preview;
        _scale = Math.Min(1, Math.Min(maxWidth / preview.Width, maxHeight / preview.Height)) * preview.Width / preview.SourceWidth;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(preview.Png);
        bitmap.EndInit();
        bitmap.Freeze();

        _canvas.Width = preview.SourceWidth * _scale;
        _canvas.Height = preview.SourceHeight * _scale;
        _canvas.Background = Brushes.Transparent;
        _canvas.Cursor = Cursors.Cross;
        _canvas.Children.Add(new Image { Source = bitmap, Width = _canvas.Width, Height = _canvas.Height, Stretch = Stretch.Fill });
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_thirds);
        _canvas.Children.Add(_frame);
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i] = new Rectangle { Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = DialogKit.Accent, IsHitTestVisible = false };
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

        BorderBrush = DialogKit.Line;
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Center;
        Child = _canvas;
    }

    /// <summary>The crop box changed (by a drag or a new ratio).</summary>
    public event EventHandler? SelectionChanged;

    public PixelRect Selection => _selection;

    /// <summary>Locks the box to width ÷ height (null = freeform) and resets it to the largest centred box.</summary>
    public void SetRatio(double? ratio)
    {
        _ratio = ratio;
        _selection = CropGeometry.Initial(_preview.SourceWidth, _preview.SourceHeight, ratio);
        Render();
    }

    /// <summary>A drag of the given handle by an amount in display units, as the mouse would do.</summary>
    internal void SimulateDrag(CropHandle handle, double dx, double dy)
    {
        BeginDrag(handle, new Point(0, 0));
        ContinueDrag(new Point(dx, dy));
        _dragging = CropHandle.None;
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
        _selection = CropGeometry.Drag(_selectionAtDragStart, _dragging, dx, dy, _ratio, _preview.SourceWidth, _preview.SourceHeight);
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
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
