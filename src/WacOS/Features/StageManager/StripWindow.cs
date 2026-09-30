using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Interop;
using WacOS.Desktop;
using WacOS.Native;

namespace WacOS.Features.StageManager;

/// <summary>
/// The left-hand strip of recent app thumbnails for one display. Like on macOS the thumbnails are drawn in
/// perspective, turned inwards towards the stage; they swing round to face the user while a window is dragged over
/// the strip (so it reads as a flat drop target) and swing back afterwards.
/// </summary>
public sealed class StripWindow : Window
{
    public const double StripWidthDip = 214, ThumbWidthDip = 176, GapDip = 24, EdgeMarginDip = 18;
    private const double TiltDegrees = 48;       // how far the thumbnails are turned towards the stage (macOS: roughly 45–50°)
    private const double CameraDistance = 540;   // smaller = stronger perspective (the far edge ends up ~80 % as tall as the near one)
    private const double Pad = 26;               // transparent border in the texture for shadows, badges and the fan

    public MonitorInfo Monitor { get; }
    private readonly StageManagerService _svc;
    private readonly Grid _root = new();
    private readonly Canvas _stage = new() { IsHitTestVisible = false };   // one small 3D viewport per thumbnail
    private readonly Canvas _canvas = new();     // transparent input surface + the drop placeholder
    private readonly List<Item> _items = new();
    private Item? _pressed, _hover; private Point _pressPoint; private bool _dragging;
    private bool _wantVisible;
    private IReadOnlyList<WindowSet> _lastSets = Array.Empty<WindowSet>();
    private ICollection<WindowSet>? _lastHidden;
    private bool _dropHint, _flat;
    private WindowSet? _dropTarget;
    private readonly TranslateTransform _slide = new();   // the whole strip slides off the left edge to get out of the way

    private sealed class Item
    {
        public WindowSet Set = null!;
        public FrameworkElement Element = null!;          // the thumbnail itself (opacity is animated on it)
        public FrameworkElement Badges = null!;           // app icon(s): flat, not tilted; they pop in a moment after the thumbnail
        public ScaleTransform BadgePop = new(1, 1);
        public Rect Rect;                                  // flat layout rectangle (DIP)
        public AxisAngleRotation3D Turn = new(new Vector3D(0, 1, 0), 0);
        public ScaleTransform3D Zoom = new(1, 1, 1);
        public TranslateTransform Shift = new();           // animated offset while the strip reflows
    }

    public double Scale { get; private set; } = 1.0;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    public double ThumbHeightDip => ThumbWidthDip * Monitor.Bounds.Height / Monitor.Bounds.Width;
    public int MaxItems => Math.Clamp((int)Math.Floor((Monitor.Bounds.Height / Scale - 2 * 60) / (ThumbHeightDip + GapDip)), 1, 6);
    public RECT ScreenRect => new(Monitor.Bounds.Left, Monitor.Bounds.Top, Monitor.Bounds.Left + (int)(StripWidthDip * Scale), Monitor.Bounds.Bottom);

    public StripWindow(StageManagerService svc, MonitorInfo mon)
    {
        _svc = svc; Monitor = mon;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; Focusable = false;
        _root.Children.Add(_stage);
        _root.Children.Add(_canvas);
        Content = _root;
        _root.RenderTransform = _slide;
        _slide.X = -StripWidthDip;
        _canvas.Background = Brushes.Transparent;
        _canvas.MouseLeftButtonDown += OnDown; _canvas.MouseMove += OnMove; _canvas.MouseLeftButtonUp += OnUp;
        _canvas.MouseLeave += (_, _) => SetHover(null);
        SourceInitialized += (_, _) =>
        {
            long ex = User32.GetWindowLong(Handle, User32.GWL_EXSTYLE) | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
            User32.SetWindowLong(Handle, User32.GWL_EXSTYLE, ex);
            Dwm.DisableTransitions(Handle, true);
        };
        new WindowInteropHelper(this).EnsureHandle();
        var b = mon.Bounds;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, b.Left, b.Top, 300, b.Height, User32.SWP_NOACTIVATE);
        uint dpi = User32.GetDpiForWindow(Handle);
        Scale = dpi > 0 ? dpi / 96.0 : mon.Scale;
        var sr = ScreenRect;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, sr.Left, sr.Top, sr.Width, sr.Height, User32.SWP_NOACTIVATE);
        Width = StripWidthDip; Height = mon.Bounds.Height / Scale;
        Opacity = 0;

    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Rebuilds the strip. Items that kept their set slide from their old slot to the new one.</summary>
    public void SetItems(IReadOnlyList<WindowSet> sets, ICollection<WindowSet>? hidden = null)
    {
        _lastSets = sets; _lastHidden = hidden;
        var oldY = _items.ToDictionary(i => i.Set, i => i.Rect.Y);
        _stage.Children.Clear();
        _canvas.Children.Clear();
        _items.Clear();
        _hover = null;
        double th = ThumbHeightDip, tw = ThumbWidthDip;
        // While a window is dragged over the strip, the first slot is reserved for it and the others make room.
        if (_dropHint && sets.Count >= MaxItems) sets = sets.Take(MaxItems - 1).ToList();
        int slots = sets.Count + (_dropHint ? 1 : 0);
        double totalH = slots * th + Math.Max(0, slots - 1) * GapDip;
        double y = (Height - totalH) / 2;
        if (_dropHint)
        {
            var ph = new Border
            {
                Width = tw, Height = th, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ph, EdgeMarginDip); Canvas.SetTop(ph, y);
            ph.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _canvas.Children.Add(ph);
            y += th + GapDip;
        }
        foreach (var set in sets)
        {
            var item = new Item { Set = set, Rect = new Rect(EdgeMarginDip, y, tw, th) };
            item.Element = MakeThumbnail(set, tw, th, out var badgePanel);
            item.Badges = badgePanel;
            item.Element.Width = tw; item.Element.Height = th;
            if (hidden != null && hidden.Contains(set)) { item.Element.Opacity = 0; item.Badges.Opacity = 0; }
            item.Turn.Angle = _flat ? 0 : TiltDegrees;
            if (oldY.TryGetValue(set, out var prev) && Math.Abs(prev - y) > 0.5)
                item.Shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(prev - y, 0, TimeSpan.FromMilliseconds(560)) { EasingFunction = new SpringEase(0.8) });
            _stage.Children.Add(MakeViewport(item));
            // The icon is not part of the tilted picture: it sits upright over the near bottom corner.
            var badgeTf = new TransformGroup();
            badgeTf.Children.Add(item.BadgePop); badgeTf.Children.Add(item.Shift);
            item.Badges.RenderTransformOrigin = new Point(0.25, 0.75);
            item.Badges.RenderTransform = badgeTf;
            Canvas.SetLeft(item.Badges, item.Rect.X - 12); Canvas.SetTop(item.Badges, item.Rect.Bottom - 24);
            _stage.Children.Add(item.Badges);
            _items.Add(item);
            if (set == _dropTarget) ZoomItem(item, 1.12);
            y += th + GapDip;
        }
    }

    /// <summary>
    /// A small 3D scene for one thumbnail: a textured quad turned about its vertical axis, seen by a camera that
    /// looks straight at its centre – so every thumbnail is the same symmetric trapezoid, wherever it sits in the strip.
    /// One world unit is one DIP on the z = 0 plane.
    /// </summary>
    private static Viewport3D MakeViewport(Item item)
    {
        double w = item.Rect.Width + 2 * Pad, h = item.Rect.Height + 2 * Pad;
        var host = new Grid { Width = w, Height = h, Background = Brushes.Transparent };
        item.Element.HorizontalAlignment = HorizontalAlignment.Center; item.Element.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(item.Element);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(-w / 2, h / 2, 0), new(-w / 2, -h / 2, 0), new(w / 2, -h / 2, 0), new(w / 2, h / 2, 0) },
            TextureCoordinates = new PointCollection { new(0, 0), new(0, 1), new(1, 1), new(1, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        var material = new DiffuseMaterial(Brushes.White);
        Viewport2DVisual3D.SetIsVisualHostMaterial(material, true);
        var tf = new Transform3DGroup();
        tf.Children.Add(item.Zoom);
        // The thumbnail swings about its left edge, which therefore stays put against the screen edge.
        tf.Children.Add(new RotateTransform3D(item.Turn, new Point3D(-item.Rect.Width / 2, 0, 0)));
        // The viewport is larger than the quad so the near edge and the hover zoom are never clipped.
        double vw = w + 2 * Pad, vh = h + 2 * Pad;
        var view = new Viewport3D
        {
            Width = vw, Height = vh, ClipToBounds = false, IsHitTestVisible = false, RenderTransform = item.Shift,
            Camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0, CameraDistance), LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 2 * Math.Atan(vw / 2 / CameraDistance) * 180 / Math.PI, NearPlaneDistance = 1, FarPlaneDistance = 5000,
            },
        };
        view.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        view.Children.Add(new Viewport2DVisual3D { Geometry = mesh, Material = material, Visual = host, Transform = tf });
        Canvas.SetLeft(view, item.Rect.X + item.Rect.Width / 2 - vw / 2);
        Canvas.SetTop(view, item.Rect.Y + item.Rect.Height / 2 - vh / 2);
        return view;
    }

    private static Grid MakeThumbnail(WindowSet set, double tw, double th, out FrameworkElement badgePanel)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        // The front window sits on the right at full height; the ones behind it step out to the left, each a bit
        // shorter, so every window of the group shows its leading edge (the way macOS folds a group).
        int n = Math.Min(set.Windows.Count, 4);
        double step = n <= 1 ? 0 : Math.Min(18, tw * 0.34 / (n - 1));
        double frameW = tw - (n - 1) * step;
        for (int i = n - 1; i >= 0; i--)
        {
            var hwnd = set.Windows[i];
            var bmp = set.Snapshots.GetValueOrDefault(hwnd) ?? WindowCapture.Cached(hwnd);
            var info = set.Info.GetValueOrDefault(hwnd);
            double h = th * (1 - 0.09 * i);
            var frame = new Border
            {
                Width = frameW, Height = h, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness((n - 1 - i) * step, 0, 0, 0),
                CornerRadius = new CornerRadius(7), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(255, 48, 48, 54)),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 180, Opacity = 0.55 },
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
            };
            if (bmp != null) frame.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            else if (info?.Icon != null) frame.Child = new Image { Source = info.Icon, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(frame);
        }
        // App icon badge(s) at the bottom-left, like macOS.
        var badges = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        foreach (var ic in set.Icons.Take(3))
            badges.Children.Add(new Image { Source = ic, Width = 38, Height = 38, Margin = new Thickness(0, 0, -12, 0), Effect = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 1, Opacity = 0.75 } });
        badgePanel = badges;
        return grid;
    }

    // ---------------------------------------------------------------- tilt

    /// <summary>Turns the thumbnails to face the user (true) or back towards the stage (false), with a spring.</summary>
    public void SetFlat(bool flat)
    {
        if (_flat == flat) return;
        _flat = flat;
        double to = flat ? 0 : TiltDegrees;
        foreach (var it in _items)
            it.Turn.BeginAnimation(AxisAngleRotation3D.AngleProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(flat ? 420 : 620)) { EasingFunction = new SpringEase(flat ? 0.9 : 0.7) });
    }

    /// <summary>Where an item actually appears on the strip (DIP): its flat rectangle seen through the camera at the current tilt.</summary>
    private Rect Projected(Rect r)
    {
        if (_flat) return r;
        double a = TiltDegrees * Math.PI / 180, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var (along, dy) in new[] { (0.0, -r.Height / 2), (r.Width, -r.Height / 2), (0.0, r.Height / 2), (r.Width, r.Height / 2) })
        {
            // "along" is measured from the pivot (the left edge); the far side recedes into the screen.
            double x = -r.Width / 2 + along * Math.Cos(a), z = -along * Math.Sin(a);
            double k = CameraDistance / (CameraDistance - z);
            double sx = cx + x * k, sy = cy + dy * k;
            minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx); minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Shows or hides the reserved first slot used as a drop target while a window is dragged over the strip.</summary>
    public void SetDropHint(bool on, bool relayout = true)
    {
        if (!on) SetDropTarget(null);
        if (_dropHint == on) return;
        _dropHint = on;
        if (relayout) SetItems(_lastSets, _lastHidden);
    }

    /// <summary>
    /// Highlights the existing item a dragged window would join (its app already has one): no slot is reserved then,
    /// because no new item will appear.
    /// </summary>
    public void SetDropTarget(WindowSet? set)
    {
        if (_dropTarget == set) return;
        var old = _items.FirstOrDefault(i => i.Set == _dropTarget);
        if (old != null && old != _hover) ZoomItem(old, 1.0);
        _dropTarget = set;
        var now = _items.FirstOrDefault(i => i.Set == set);
        if (now != null) ZoomItem(now, 1.12);
    }

    public bool DropHint => _dropHint;

    /// <summary>Fades in an item that was kept hidden while its windows were flying towards it.</summary>
    public void RevealItem(WindowSet set)
    {
        var it = _items.FirstOrDefault(i => i.Set == set);
        if (it == null) return;
        it.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
        PopBadges(it, 0);
    }

    /// <summary>
    /// Takes over from windows flying into the strip. A flying window is a flat picture (the compositor cannot draw
    /// it in perspective), so the thumbnail fades in facing the user exactly where the window is settling, and then
    /// swings round into its tilted resting pose – one continuous motion instead of a sudden change of angle.
    /// The app icon pops in a moment later.
    /// </summary>
    public void ArriveItem(WindowSet set)
    {
        var it = _items.FirstOrDefault(i => i.Set == set);
        if (it == null) return;
        it.Turn.BeginAnimation(AxisAngleRotation3D.AngleProperty, null);
        it.Turn.Angle = 0;
        it.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        if (!_flat)
            it.Turn.BeginAnimation(AxisAngleRotation3D.AngleProperty,
                new DoubleAnimation(0, TiltDegrees, TimeSpan.FromMilliseconds(640)) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = new SpringEase(0.74) });
        PopBadges(it, 260);
    }

    private static void PopBadges(Item it, double delayMs)
    {
        if (it.Badges.Opacity >= 1 && delayMs <= 0) return;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        it.Badges.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin });
        var st = it.BadgePop;
        {
            var pop = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = begin, EasingFunction = new SpringEase(0.6) };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop); st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>Screen-pixel rectangle of the slot showing a set as it appears on screen (top slot when not shown).</summary>
    public RECT SlotRect(WindowSet? set)
    {
        var it = set == null ? null : _items.FirstOrDefault(i => i.Set == set);
        Rect flat = it?.Rect ?? (_items.Count > 0 ? _items[0].Rect : new Rect(EdgeMarginDip, (Height - ThumbHeightDip) / 2, ThumbWidthDip, ThumbHeightDip));
        var r = Projected(flat);
        return new RECT(Monitor.Bounds.Left + (int)(r.X * Scale), Monitor.Bounds.Top + (int)(r.Y * Scale), Monitor.Bounds.Left + (int)((r.X + r.Width) * Scale), Monitor.Bounds.Top + (int)((r.Y + r.Height) * Scale));
    }

    public bool HasSlot(WindowSet set) => _items.Any(i => i.Set == set);

    public WindowSet? SetAtScreenPoint(int x, int y)
    {
        if (!_wantVisible) return null;
        return Hit(new Point((x - Monitor.Bounds.Left) / Scale, (y - Monitor.Bounds.Top) / Scale))?.Set;
    }

    // ---------------------------------------------------------------- visibility

    public bool IsShown => _wantVisible;

    public void SetVisible(bool visible)
    {
        if (_wantVisible == visible) return;
        _wantVisible = visible;
        if (visible)
        {
            // Slides in from beyond the left edge.
            Show();
            User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase(0.86) });
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }
        else
        {
            // Retreats to the left, out of the way of the window that needs the space.
            var a = new DoubleAnimation(-StripWidthDip - 20, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            a.Completed += (_, _) => { if (!_wantVisible) Hide(); };
            _slide.BeginAnimation(TranslateTransform.XProperty, a);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        }
    }

    /// <summary>The slot of a set as seen from outside: off the left edge while the strip has retreated.</summary>
    public RECT SlotOnScreen(WindowSet? set, bool flat = false)
    {
        RECT r;
        if (flat)
        {
            // The un-tilted rectangle: where a flat window picture has to land to match a thumbnail facing the user.
            var it = set == null ? null : _items.FirstOrDefault(i => i.Set == set);
            Rect f = it?.Rect ?? (_items.Count > 0 ? _items[0].Rect : new Rect(EdgeMarginDip, (Height - ThumbHeightDip) / 2, ThumbWidthDip, ThumbHeightDip));
            r = new RECT(Monitor.Bounds.Left + (int)(f.X * Scale), Monitor.Bounds.Top + (int)(f.Y * Scale), Monitor.Bounds.Left + (int)((f.X + f.Width) * Scale), Monitor.Bounds.Top + (int)((f.Y + f.Height) * Scale));
        }
        else r = SlotRect(set != null && HasSlot(set) ? set : null);
        if (_wantVisible) return r;
        int w = ScreenRect.Width + (int)(20 * Scale);
        return new RECT(r.Left - w, r.Top, r.Right - w, r.Bottom);
    }

    // ---------------------------------------------------------------- interaction

    private Item? Hit(Point p) => _items.FirstOrDefault(i => Projected(i.Rect).Contains(p));

    private void SetHover(Item? item)
    {
        if (_hover == item) return;
        if (_hover != null) ZoomItem(_hover, 1.0);
        _hover = item;
        if (item == null) { _canvas.Cursor = Cursors.Arrow; _canvas.ToolTip = null; return; }
        ZoomItem(item, 1.08);
        _canvas.Cursor = Cursors.Hand;
        var set = item.Set;
        _canvas.ToolTip = set.Windows.Count > 1 ? L.F("{0} – {1} windows", set.AppName, set.Windows.Count) : (set.Info.GetValueOrDefault(set.Front)?.Title ?? set.AppName);
    }

    private static void ZoomItem(Item it, double s)
    {
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(320)) { EasingFunction = new SpringEase(0.62) };
        it.Zoom.BeginAnimation(ScaleTransform3D.ScaleXProperty, a); it.Zoom.BeginAnimation(ScaleTransform3D.ScaleYProperty, a);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        _pressed = Hit(p); _pressPoint = p; _dragging = false;
        if (_pressed != null) { _canvas.CaptureMouse(); e.Handled = true; }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) { SetHover(Hit(p)); return; }
        if (!_dragging && (Math.Abs(p.X - _pressPoint.X) > 8 || Math.Abs(p.Y - _pressPoint.Y) > 8))
        {
            // The live windows take over from the thumbnail and follow the pointer.
            _dragging = true;
            SetHover(null);
            _pressed.Element.BeginAnimation(OpacityProperty, null);
            _pressed.Element.Opacity = 0;
            _pressed.Badges.BeginAnimation(OpacityProperty, null); _pressed.Badges.Opacity = 0;
            _svc.BeginThumbDrag(Monitor, _pressed.Set);
        }
        if (_dragging) { User32.GetCursorPos(out var cp); _svc.MoveThumbDrag(cp.X, cp.Y); }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        var pressed = _pressed; _pressed = null;
        if (pressed == null) return;
        var p = e.GetPosition(_canvas);
        if (_dragging)
        {
            _dragging = false;
            // Dropped on the stage (right of the strip) → it lands there and joins the current group;
            // otherwise the windows fly back into their slot (which fades the thumbnail in again).
            User32.GetCursorPos(out var cp);
            _svc.EndThumbDrag(cp.X, cp.Y);
            return;
        }
        if (Hit(p) != pressed) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 || User32.IsKeyDown(User32.VK_SHIFT);
        if (shift) _svc.MergeToStage(Monitor, pressed.Set);
        else _svc.BringToStage(Monitor, pressed.Set);
    }
}

/// <summary>WPF easing function backed by the same damped spring the window animations use.</summary>
public sealed class SpringEase : EasingFunctionBase
{
    private readonly double _zeta;
    private readonly Func<double, double> _f;
    public SpringEase(double zeta) { _zeta = zeta; _f = Easing.SpringOf(zeta); EasingMode = EasingMode.EaseIn; }
    protected override double EaseInCore(double normalizedTime) => _f(normalizedTime);
    protected override Freezable CreateInstanceCore() => new SpringEase(_zeta);
}
