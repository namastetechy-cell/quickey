using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Quickey.App.Native;
using Quickey.Core.Operations;
using Quickey.Core.Settings;

namespace Quickey.App.UI;

public sealed class OperationChosenEventArgs(IOperation operation, IReadOnlyList<string> files) : EventArgs
{
    public IOperation Operation { get; } = operation;
    public IReadOnlyList<string> Files { get; } = files;
}

/// <summary>
/// The radial menu. In drag mode it is an OLE drop target that pops up under the cursor mid-drag:
/// dropping onto a wedge runs that operation. In click mode (from Explorer's right-click menu)
/// the files are already known and a click picks the wedge.
/// </summary>
public sealed class WheelWindow : Window
{
    private const double Size = 380;
    private const double Center = Size / 2;
    private const double InnerRadius = 62;
    private const double OuterRadius = 172;
    private const double GapDegrees = 1.6;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Brush WedgeFill = Frozen(Color.FromArgb(0xF2, 0x26, 0x24, 0x3A));
    private static readonly Brush WedgeHover = Frozen(Color.FromRgb(0x63, 0x66, 0xF1));
    private static readonly Brush DiscFill = Frozen(Color.FromArgb(0xF7, 0x16, 0x15, 0x24));
    private static readonly Brush TextBrush = Frozen(Colors.White);
    private static readonly Brush HintBrush = Frozen(Color.FromRgb(0xA5, 0xA3, 0xC2));

    private readonly OperationRegistry _registry;
    private readonly Func<AppSettings> _settings;
    private readonly Canvas _canvas = new() { Width = Size, Height = Size };
    private readonly ScaleTransform _scale = new(1, 1, Center, Center);
    private readonly TextBlock _title = new() { Foreground = TextBrush, FontSize = 17, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _hint = new() { Foreground = HintBrush, FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = InnerRadius * 2 - 16 };
    private readonly DispatcherTimer _hideAfterRelease = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly List<Wedge> _wedges = [];

    private OperationCategory _category;
    private IReadOnlyList<string> _files = [];
    private bool _clickMode;
    private int _hovered = -1;
    private IntPtr _hwnd;

    public WheelWindow(OperationRegistry registry, Func<AppSettings> settings)
    {
        _registry = registry;
        _settings = settings;

        Title = "Quickey";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        Width = Height = Size;
        SizeToContent = SizeToContent.Manual;

        _canvas.RenderTransform = _scale;
        Content = _canvas;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetHovered(-1);
        Drop += OnDrop;
        MouseMove += (_, e) => { if (_clickMode) SetHovered(HitTest(e.GetPosition(_canvas))); };
        MouseLeftButtonUp += OnClick;
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => { if (_clickMode) HideWheel(); };
        LostMouseCapture += (_, _) => { if (_clickMode && IsOpen && !IsMouseCaptured) HideWheel(); };
        _hideAfterRelease.Tick += (_, _) => HideWheel();
    }

    public event EventHandler<OperationChosenEventArgs>? OperationChosen;

    public bool IsOpen => IsVisible;

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = Handle;
        // Never steal focus from Explorer mid-drag, and stay out of Alt+Tab.
        Win32.AddExStyle(Handle, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    /// <summary>Opens the wheel mid-drag. Files become known when the drag enters the window.</summary>
    public void ShowForDrag(OperationCategory category, Win32.POINT cursor)
    {
        _hideAfterRelease.Stop();
        _clickMode = false;
        if (IsOpen)
        {
            // Another modifier tapped while open: switch wheels in place.
            SetCategory(category);
            return;
        }
        _files = [];
        SetCategory(category);
        Present(cursor);
    }

    /// <summary>Opens the wheel for files picked from Explorer's right-click menu.</summary>
    public void ShowForFiles(IReadOnlyList<string> files, OperationCategory category, Win32.POINT cursor)
    {
        _hideAfterRelease.Stop();
        _clickMode = true;
        _files = files;
        SetCategory(category);
        Present(cursor);
        Win32.SetForegroundWindow(Handle);
        Activate();
        Focus();
        // With capture, a click outside the wheel reaches us (and closes it) even if focus was refused.
        CaptureMouse();
    }

    /// <summary>The drag ended. A drop may still be on its way, so give it a moment before closing.</summary>
    public void OnDragReleased()
    {
        if (IsOpen && !_clickMode)
            _hideAfterRelease.Start();
    }

    public void HideWheel()
    {
        _hideAfterRelease.Stop();
        _clickMode = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        SetHovered(-1);
        Hide();
    }

    /// <summary>
    /// True if a screen point is over the open wheel (drags starting there aren't file drags).
    /// Called from the input hook thread, so it only touches the native window, never WPF state.
    /// </summary>
    public bool Contains(Win32.POINT point)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd) || !Win32.GetWindowRect(hwnd, out var rect))
            return false;
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private void Present(Win32.POINT cursor)
    {
        Show();
        Win32.CenterOn(Handle, cursor);
        if (SystemParameters.ClientAreaAnimation)
        {
            var grow = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private void SetCategory(OperationCategory category)
    {
        _category = category;
        Rebuild();
    }

    private void SetFiles(IReadOnlyList<string> files)
    {
        if (files.SequenceEqual(_files))
            return;
        _files = files;
        Rebuild();
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        _wedges.Clear();
        _hovered = -1;

        var disc = new Ellipse { Width = OuterRadius * 2 + 12, Height = OuterRadius * 2 + 12, Fill = DiscFill };
        disc.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45 };
        Place(disc, Center - disc.Width / 2, Center - disc.Height / 2);

        var operations = _files.Count == 0 ? [] : _registry.For(_category, _files);
        var sweep = operations.Count == 0 ? 0 : 360.0 / operations.Count;
        for (var i = 0; i < operations.Count; i++)
        {
            // First wedge is centred at 12 o'clock, then clockwise.
            var start = -90 - sweep / 2 + i * sweep;
            var wedge = new Wedge(operations[i], start, sweep);
            _wedges.Add(wedge);
            _canvas.Children.Add(wedge.Shape);
            AddLabel(wedge);
        }

        var hub = new Ellipse { Width = InnerRadius * 2 - 6, Height = InnerRadius * 2 - 6, Fill = WedgeFill };
        Place(hub, Center - hub.Width / 2, Center - hub.Height / 2);
        var centerText = new StackPanel { Width = InnerRadius * 2, Children = { _title, _hint } };
        _canvas.Children.Add(centerText);
        centerText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(centerText, Center - InnerRadius);
        Canvas.SetTop(centerText, Center - centerText.DesiredSize.Height / 2);

        UpdateCenterText();
    }

    private void AddLabel(Wedge wedge)
    {
        var mid = (wedge.Start + wedge.Sweep / 2) * Math.PI / 180;
        var radius = (InnerRadius + OuterRadius) / 2;
        var panel = new StackPanel
        {
            IsHitTestVisible = false,
            Opacity = wedge.Operation.UnavailableReason is null ? 1 : 0.4,
            Children =
            {
                new TextBlock { Text = wedge.Operation.Glyph, FontFamily = IconFont, FontSize = 20, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = wedge.Operation.Label, FontSize = 12, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) },
            },
        };
        _canvas.Children.Add(panel);
        panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(panel, Center + radius * Math.Cos(mid) - panel.DesiredSize.Width / 2);
        Canvas.SetTop(panel, Center + radius * Math.Sin(mid) - panel.DesiredSize.Height / 2);
    }

    private void UpdateCenterText()
    {
        var settings = _settings();
        _title.Text = _category.ToString();

        if (_hovered >= 0 && _wedges[_hovered].Operation.UnavailableReason is { } reason)
            _hint.Text = reason;
        else if (_files.Count == 0 && !_clickMode)
            _hint.Text = "Move here to see options";
        else if (_wedges.Count == 0)
            _hint.Text = $"Nothing to {_category.ToString().ToLowerInvariant()} for {Describe(_files)}";
        else
            _hint.Text = $"{Describe(_files)}\n{settings.ConvertKey} · {settings.ToolsKey} · {settings.ActionsKey}";
    }

    private static string Describe(IReadOnlyList<string> files) =>
        files.Count == 1 ? System.IO.Path.GetExtension(files[0]).TrimStart('.').ToUpperInvariant() + " file" : $"{files.Count} files";

    private void SetHovered(int index)
    {
        if (index == _hovered)
            return;
        if (_hovered >= 0 && _hovered < _wedges.Count)
            _wedges[_hovered].Shape.Fill = WedgeFill;
        _hovered = index;
        if (_hovered >= 0 && _wedges[_hovered].Operation.UnavailableReason is null)
            _wedges[_hovered].Shape.Fill = WedgeHover;
        UpdateCenterText();
    }

    /// <summary>Which wedge a point (in canvas coordinates) is over, or -1 for the hub and outside.</summary>
    private int HitTest(Point point)
    {
        var dx = point.X - Center;
        var dy = point.Y - Center;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        // A little slack past the rim so a fast flick still lands.
        if (_wedges.Count == 0 || distance < InnerRadius || distance > OuterRadius + 24)
            return -1;
        var sweep = 360.0 / _wedges.Count;
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        var fromStart = ((angle - (-90 - sweep / 2)) % 360 + 360) % 360;
        return Math.Min((int)(fromStart / sweep), _wedges.Count - 1);
    }

    private IOperation? Runnable(int index) =>
        index >= 0 && _wedges[index].Operation.UnavailableReason is null ? _wedges[index].Operation : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _hideAfterRelease.Stop();
        SetFiles(e.Data.GetData(DataFormats.FileDrop) as string[] ?? []);
        SetHovered(HitTest(e.GetPosition(_canvas)));
        // Always Copy, never Move: a Move effect would make Explorer delete the originals after the drop.
        e.Effects = Runnable(_hovered) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var operation = Runnable(HitTest(e.GetPosition(_canvas)));
        e.Effects = operation is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        HideWheel();
        if (operation is not null && files.Length > 0)
            OperationChosen?.Invoke(this, new OperationChosenEventArgs(operation, files));
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (!_clickMode)
            return;
        var files = _files;
        var operation = Runnable(HitTest(e.GetPosition(_canvas)));
        if (operation is null && _hovered >= 0)
            return; // Greyed-out wedge: keep the wheel open so the reason stays readable.
        HideWheel();
        if (operation is not null)
            OperationChosen?.Invoke(this, new OperationChosenEventArgs(operation, files));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            HideWheel();
            return;
        }
        ModifierKey? modifier = key switch
        {
            Key.LeftShift or Key.RightShift => ModifierKey.Shift,
            Key.LeftCtrl or Key.RightCtrl => ModifierKey.Ctrl,
            Key.LeftAlt or Key.RightAlt => ModifierKey.Alt,
            _ => null,
        };
        if (modifier is { } m && _settings().CategoryFor(m) is { } category)
        {
            SetCategory(category);
            e.Handled = true;
        }
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        _canvas.Children.Add(element);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Wedge
    {
        public Wedge(IOperation operation, double start, double sweep)
        {
            Operation = operation;
            Start = start;
            Sweep = sweep;
            Shape = new Path { Data = Geometry(start, sweep), Fill = WedgeFill, Opacity = operation.UnavailableReason is null ? 1 : 0.55 };
        }

        public IOperation Operation { get; }
        public double Start { get; }
        public double Sweep { get; }
        public Path Shape { get; }

        private static Geometry Geometry(double start, double sweep)
        {
            if (sweep >= 360)
            {
                // A single operation gets the whole ring.
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(Center, Center), OuterRadius, OuterRadius),
                    new EllipseGeometry(new Point(Center, Center), InnerRadius, InnerRadius));
                ring.Freeze();
                return ring;
            }

            var a0 = (start + GapDegrees / 2) * Math.PI / 180;
            var a1 = (start + sweep - GapDegrees / 2) * Math.PI / 180;
            var large = sweep - GapDegrees > 180;
            var figure = new PathFigure { StartPoint = At(InnerRadius, a0), IsClosed = true };
            figure.Segments.Add(new LineSegment(At(OuterRadius, a0), true));
            figure.Segments.Add(new ArcSegment(At(OuterRadius, a1), new System.Windows.Size(OuterRadius, OuterRadius), 0, large, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(At(InnerRadius, a1), true));
            figure.Segments.Add(new ArcSegment(At(InnerRadius, a0), new System.Windows.Size(InnerRadius, InnerRadius), 0, large, SweepDirection.Counterclockwise, true));
            var geometry = new PathGeometry([figure]);
            geometry.Freeze();
            return geometry;
        }

        private static Point At(double radius, double radians) =>
            new(Center + radius * Math.Cos(radians), Center + radius * Math.Sin(radians));
    }
}
