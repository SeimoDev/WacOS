using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Native;
using WacOS.VirtualDesktops;

namespace WacOS.Features.MissionControl;

public sealed class SpaceDropTarget
{
    public Space? Space;
    public bool IsAddButton;
}

public enum EnterKind { FromDesktop, FadeIn }

/// <summary>
/// One Mission Control / App Exposé overlay per monitor. Windows are shown as live DWM thumbnails composited by the
/// GPU on top of an opaque WPF window (backdrop, Spaces bar, labels), so every transition runs at display refresh rate.
/// </summary>
public sealed class MissionControlWindow : OverlayWindow
{
    private readonly MissionControlService _svc;
    private readonly VirtualDesktopService _vd;
    private MissionControlModel _model = new();
    private MissionControlMode _mode;

    // visual tree
    private readonly Grid _root = new();
    private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill };
    private readonly Rectangle _dim = new() { Fill = new SolidColorBrush(Color.FromRgb(12, 13, 18)), Opacity = 0, IsHitTestVisible = false };
    private readonly Canvas _under = new() { IsHitTestVisible = false };
    private readonly Border _hoverFrame = new();
    private readonly Grid _hit = new() { Background = Brushes.Transparent };
    private readonly Border _bar = new();
    private readonly TranslateTransform _barShift = new();
    private readonly StackPanel _barItems = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _label = new();
    private readonly Border _header = new();
    private readonly DispatcherTimer _poll;

    // state
    private readonly List<Tile> _tileList = new();
    private readonly List<BarItem> _barList = new();
    private readonly List<Thumb> _taskbarThumbs = new();
    private readonly List<Rect> _taskbarRects = new();
    private Thumb? _dragThumb;
    private bool _barExpanded, _closing, _altShown, _quickLookOn;
    private Tile? _hover, _pressed, _dragging, _quickLookTile;
    private Point _pressPoint;
    private BarItem? _barPressed, _barDragging;
    private double _barDragStartX;
    private double _bottomLimit;
    private AnimHandle? _mainAnim;
    private double _chrome;

    public IntPtr DragHwnd { get; private set; }

    private const double BarCollapsed = 36, ThumbHeightRatio = 0.10, BarPad = 14, TileGap = 30, RowGap = 56, DimMax = 0.42;
    private const double EnterMs = 540, ExitMs = 400;

    private sealed class Tile
    {
        public List<WindowInfo> Windows = new();   // front-most first; >1 when grouped by app
        public List<Thumb?> Thumbs = new();
        public WindowInfo Primary => Windows[0];
        public Rect Target;                        // layout rect (DIP)
        public bool IsMinimizedRow;
        public FrameworkElement? Placeholder;
        public AnimHandle? Anim;
    }

    private sealed class BarItem
    {
        public Space? Space;
        public bool IsAdd;
        public Border Element = null!;
        public Border Close = null!;
        public Border Thumb = null!;
        public FrameworkElement ThumbHost = null!;
        public Image? Img;
        public TextBlock Name = null!;
        public Rect Rect;
    }

    public MissionControlWindow(MissionControlService svc, VirtualDesktopService vd, MonitorInfo mon)
    {
        _svc = svc; _vd = vd;
        PlaceOnMonitor(mon);
        BuildTree();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += (_, _) => PollTick();
        KeyDown += OnKeyDown; KeyUp += OnKeyUp;
        Focusable = true;
        // Upload the desktop picture to the GPU while the window is still cloaked, so the first reveal is instant.
        DesktopBackdrop.Updated += () => { if (!IsRevealed) ApplyBackdrop(); };
        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        var bd = DesktopBackdrop.Get(Monitor);
        if (bd != null) { if (!ReferenceEquals(_backdrop.Source, bd)) { _backdrop.Source = bd; _backdrop.Stretch = Stretch.Fill; } }
        else if (_backdrop.Source == null)
        {
            _backdrop.Source = Wallpaper.Load(null, Monitor.Bounds.Width);
            _backdrop.Stretch = Stretch.UniformToFill;
        }
    }

    // ------------------------------------------------------------------ static tree

    private void BuildTree()
    {
        Content = _root;
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.LowQuality);
        _root.Children.Add(_backdrop);
        _root.Children.Add(_dim);

        _hoverFrame.BorderBrush = new SolidColorBrush(Color.FromArgb(235, 110, 165, 255));
        _hoverFrame.BorderThickness = new Thickness(3.5);
        _hoverFrame.CornerRadius = new CornerRadius(9);
        _hoverFrame.Visibility = Visibility.Collapsed;
        _under.Children.Add(_hoverFrame);
        _root.Children.Add(_under);

        _hit.MouseLeftButtonDown += Hit_MouseDown;
        _hit.MouseMove += Hit_MouseMove;
        _hit.MouseLeftButtonUp += Hit_MouseUp;
        _hit.MouseLeave += (_, _) => { if (_dragging == null) SetHover(null); };
        _root.Children.Add(_hit);

        _bar.VerticalAlignment = VerticalAlignment.Top;
        _bar.Height = BarCollapsed;
        _bar.Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        _bar.RenderTransform = _barShift;
        _bar.MouseEnter += (_, _) => ExpandBar(true);
        _bar.MouseLeave += (_, _) => { if (_dragging == null && DragHwnd == IntPtr.Zero && _barDragging == null) ExpandBar(false); };
        _bar.MouseLeftButtonDown += Bar_MouseDown;
        _bar.MouseMove += Bar_MouseMove;
        _bar.MouseLeftButtonUp += Bar_MouseUp;
        _bar.Child = _barItems;
        _root.Children.Add(_bar);

        _header.HorizontalAlignment = HorizontalAlignment.Center; _header.VerticalAlignment = VerticalAlignment.Top; _header.Margin = new Thickness(0, 22, 0, 0);
        _header.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)); _header.CornerRadius = new CornerRadius(10); _header.Padding = new Thickness(14, 8, 14, 8);
        _header.IsHitTestVisible = false;
        _root.Children.Add(_header);

        _label.Background = new SolidColorBrush(Color.FromArgb(215, 30, 30, 34)); _label.CornerRadius = new CornerRadius(7); _label.Padding = new Thickness(11, 6, 11, 6);
        _label.Visibility = Visibility.Collapsed; _label.IsHitTestVisible = false; _label.HorizontalAlignment = HorizontalAlignment.Left; _label.VerticalAlignment = VerticalAlignment.Top;
        _root.Children.Add(_label);
    }

    private double ThumbHeight => Math.Max(70, Height * ThumbHeightRatio);
    private double ThumbWidth => ThumbHeight * (Width / Height);
    private double BarExpanded => ThumbHeight + BarPad * 2 + 22;
    public (int w, int h) SpaceThumbPixels => ((int)Math.Round(ThumbWidth * Scale), (int)Math.Round(ThumbHeight * Scale));

    // ------------------------------------------------------------------ prepare (build content for one session)

    public void Prepare(MissionControlModel model, MissionControlMode mode, IntPtr dragHwnd, EnterKind kind, Func<Guid, ImageSource?> cachedSpaceThumb)
    {
        _mainAnim?.Cancel();
        DisposeThumbs();
        bool keepExpanded = kind == EnterKind.FadeIn && _barExpanded;
        _model = model; _mode = mode; DragHwnd = dragHwnd; _closing = false;
        _hover = _pressed = _dragging = _quickLookTile = null; _quickLookOn = false; _barPressed = _barDragging = null;
        _hoverFrame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed;

        ApplyBackdrop();

        FindTaskbars();
        if (mode == MissionControlMode.Full)
        {
            _bar.Visibility = Visibility.Visible; _header.Visibility = Visibility.Collapsed;
            _barExpanded = keepExpanded || dragHwnd != IntPtr.Zero;
            BuildBar(cachedSpaceThumb);
        }
        else
        {
            _bar.Visibility = Visibility.Collapsed; _header.Visibility = Visibility.Visible;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var fw = model.Windows.FirstOrDefault(w => w.AppKey == model.FrontAppKey);
            if (fw?.Icon != null) sp.Children.Add(new Image { Source = fw.Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = model.FrontAppName ?? "", Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI") });
            _header.Child = sp;
        }

        BuildTiles();
        LayoutTiles();
        RegisterThumbs(kind);
        SetChrome(kind == EnterKind.FromDesktop ? 0 : 1);
    }

    private void FindTaskbars()
    {
        _taskbarRects.Clear();
        _bottomLimit = Height;
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = User32.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                if (!User32.IsWindowVisible(h) || !User32.GetWindowRect(h, out var r)) continue;
                var mb = Monitor.Bounds;
                if (r.Left >= mb.Right || r.Right <= mb.Left || r.Top >= mb.Bottom || r.Bottom <= mb.Top) continue;
                var th = Thumb.Create(Handle, h);
                if (th == null) continue;
                th.Set(Rel(r), 255);
                _taskbarThumbs.Add(th);
                var local = ToLocal(r);
                _taskbarRects.Add(local);
                if (local.Y > Height / 2) _bottomLimit = Math.Min(_bottomLimit, local.Y);
            }
        }
    }

    private void DisposeThumbs()
    {
        foreach (var t in _tileList) { t.Anim?.Cancel(); foreach (var th in t.Thumbs) th?.Dispose(); if (t.Placeholder != null) _under.Children.Remove(t.Placeholder); }
        _tileList.Clear();
        foreach (var th in _taskbarThumbs) th.Dispose();
        _taskbarThumbs.Clear();
        _dragThumb?.Dispose(); _dragThumb = null;
    }

    // ------------------------------------------------------------------ spaces bar

    private void BuildBar(Func<Guid, ImageSource?> cached)
    {
        _barItems.Children.Clear();
        _barList.Clear();
        foreach (var sp in _model.Spaces)
        {
            var item = MakeBarItem(sp, cached(sp.Id));
            _barList.Add(item);
            _barItems.Children.Add(item.Element);
        }
        var add = MakeAddItem();
        _barList.Add(add);
        _barItems.Children.Add(add.Element);
        ApplyBarState(animate: false);
    }

    private BarItem MakeBarItem(Space sp, ImageSource? cachedThumb)
    {
        var item = new BarItem { Space = sp };
        var stack = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var thumbGrid = new Grid { Width = ThumbWidth, Height = ThumbHeight, Margin = new Thickness(0, BarPad, 0, 4) };
        bool current = sp.Id == _model.CurrentSpace;
        var img = new Image { Source = cachedThumb, Stretch = Stretch.Fill };
        var thumb = new Border
        {
            CornerRadius = new CornerRadius(6), ClipToBounds = true, BorderThickness = new Thickness(current ? 2.5 : 1),
            BorderBrush = current ? Brushes.White : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            Background = Wallpaper.BackdropBrush(string.IsNullOrEmpty(sp.WallpaperPath) ? null : sp.WallpaperPath),
            Child = img, SnapsToDevicePixels = true,
        };
        thumbGrid.Children.Add(thumb);
        var close = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(235, 60, 60, 66)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-8, -8, 0, 0),
            Visibility = Visibility.Collapsed, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        close.MouseLeftButtonDown += (s, e) => { e.Handled = true; RemoveSpace(sp); };
        thumbGrid.Children.Add(close);
        stack.Children.Add(thumbGrid);
        var name = new TextBlock
        {
            Text = sp.DisplayName, Foreground = Brushes.White, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ThumbWidth + 10,
        };
        stack.Children.Add(name);
        var border = new Border { Child = stack, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        border.MouseEnter += (_, _) => { if (_barExpanded) item.Close.Visibility = _model.Spaces.Count > 1 ? Visibility.Visible : Visibility.Collapsed; };
        border.MouseLeave += (_, _) => { if (!IsAltDown()) item.Close.Visibility = Visibility.Collapsed; };
        item.Element = border; item.Close = close; item.Thumb = thumb; item.ThumbHost = thumbGrid; item.Name = name; item.Img = img;
        return item;
    }

    private BarItem MakeAddItem()
    {
        var item = new BarItem { IsAdd = true };
        var stack = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        var thumb = new Border
        {
            Width = ThumbWidth * 0.6, Height = ThumbHeight, Margin = new Thickness(0, BarPad, 0, 4), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "+", Foreground = Brushes.White, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        stack.Children.Add(thumb);
        var name = new TextBlock { Text = "+", Foreground = Brushes.White, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(name);
        var border = new Border { Child = stack, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("Add a new desktop") };
        item.Element = border; item.Thumb = thumb; item.ThumbHost = thumb; item.Name = name; item.Close = new Border();
        return item;
    }

    private void ApplyBarState(bool animate)
    {
        foreach (var it in _barList)
        {
            it.ThumbHost.Visibility = _barExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (!_barExpanded) it.Close.Visibility = Visibility.Collapsed;
            it.Name.Margin = _barExpanded ? new Thickness(0, 0, 0, 6) : new Thickness(0, 9, 0, 6);
            if (it.IsAdd) it.Name.Visibility = _barExpanded ? Visibility.Collapsed : Visibility.Visible;
        }
        double h = _barExpanded ? BarExpanded : BarCollapsed;
        if (animate)
            _bar.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(380)) { EasingFunction = new WacOS.Features.StageManager.SpringEase(0.82) });
        else { _bar.BeginAnimation(HeightProperty, null); _bar.Height = h; }
    }

    private void ExpandBar(bool expand)
    {
        if (_barExpanded == expand || _mode != MissionControlMode.Full) return;
        _barExpanded = expand;
        ApplyBarState(animate: true);
    }

    public void SetSpaceThumbs(IReadOnlyDictionary<Guid, ImageSource> thumbs)
    {
        foreach (var it in _barList)
            if (it.Space != null && it.Img != null && thumbs.TryGetValue(it.Space.Id, out var img)) it.Img.Source = img;
    }

    /// <summary>Composes the miniature of every space for one monitor. Runs on the worker thread.</summary>
    public static Dictionary<Guid, ImageSource> RenderSpaceThumbs(MissionControlModel model, MonitorInfo mon, int w, int h)
    {
        var result = new Dictionary<Guid, ImageSource>();
        w = Math.Max(w, 16); h = Math.Max(h, 16);
        var mb = mon.Bounds;
        double sx = (double)w / mb.Width, sy = (double)h / mb.Height;
        var shots = new Dictionary<IntPtr, BitmapSource?>();
        foreach (var win in model.Windows)
        {
            if (win.IsMinimized || win.Monitor != mon.Handle) continue;
            shots[win.Hwnd] = WindowCapture.Capture(win.Hwnd, allowCached: true, maxAgeMs: 1500) ?? WindowCapture.Cached(win.Hwnd);
        }
        foreach (var sp in model.Spaces)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                ImageSource? wp = sp.Id == model.CurrentSpace ? DesktopBackdrop.Get(mon) : null;
                wp ??= Wallpaper.Load(string.IsNullOrEmpty(sp.WallpaperPath) ? null : sp.WallpaperPath, 400);
                if (wp != null) dc.DrawImage(wp, new Rect(0, 0, w, h)); else dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(50, 55, 70)), null, new Rect(0, 0, w, h));
                foreach (var win in Enumerable.Reverse(model.Windows))
                {
                    if (win.IsMinimized || win.Monitor != mon.Handle) continue;
                    bool onSpace = model.Pinned.Contains(win.Hwnd) || model.WindowSpace.GetValueOrDefault(win.Hwnd) == sp.Id;
                    if (!onSpace) continue;
                    var r = new Rect((win.Bounds.Left - mb.Left) * sx, (win.Bounds.Top - mb.Top) * sy, win.Bounds.Width * sx, win.Bounds.Height * sy);
                    var bmp = shots.GetValueOrDefault(win.Hwnd);
                    if (bmp != null) dc.DrawImage(bmp, r); else dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 90, 90, 100)), null, r);
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            result[sp.Id] = rtb;
        }
        return result;
    }

    // ------------------------------------------------------------------ tiles

    private void BuildTiles()
    {
        var wins = _model.Windows.Where(w => w.Monitor == Monitor.Handle).ToList();
        if (_mode == MissionControlMode.Full)
        {
            wins = wins.Where(w => !w.IsMinimized && !w.IsCloaked && (_model.Pinned.Contains(w.Hwnd) || _model.WindowSpace.GetValueOrDefault(w.Hwnd) == _model.CurrentSpace)).ToList();
            if (DragHwnd != IntPtr.Zero) wins = wins.Where(w => w.Hwnd != DragHwnd).ToList();
            if (App.Settings.Current.GroupWindowsByApplication)
                foreach (var g in wins.GroupBy(w => w.AppKey)) _tileList.Add(new Tile { Windows = g.ToList() });
            else foreach (var w in wins) _tileList.Add(new Tile { Windows = { w } });
        }
        else
        {
            wins = wins.Where(w => w.AppKey == _model.FrontAppKey).ToList();
            foreach (var w in wins.Where(w => !w.IsMinimized)) _tileList.Add(new Tile { Windows = { w } });
            foreach (var w in wins.Where(w => w.IsMinimized)) _tileList.Add(new Tile { Windows = { w }, IsMinimizedRow = true });
        }
    }

    private Rect TileArea()
    {
        double top = (_mode == MissionControlMode.Full ? BarExpanded : 70) + 28;
        double bottom = _bottomLimit - 56;
        if (_mode == MissionControlMode.AppExpose && _tileList.Any(t => t.IsMinimizedRow)) bottom -= 150;
        return new Rect(60, top, Math.Max(100, Width - 120), Math.Max(100, bottom - top));
    }

    private void LayoutTiles()
    {
        LayoutInto(_tileList.Where(t => !t.IsMinimizedRow).ToList(), TileArea(), maxScale: 1.0);
        var minis = _tileList.Where(t => t.IsMinimizedRow).ToList();
        if (minis.Count > 0) LayoutInto(minis, new Rect(60, _bottomLimit - 180, Width - 120, 130), maxScale: 0.35);
    }

    private void LayoutInto(List<Tile> tiles, Rect area, double maxScale)
    {
        if (tiles.Count == 0) return;
        var size = new Dictionary<Tile, Size>();
        foreach (var t in tiles)
        {
            double w = t.Windows.Max(x => x.Bounds.Width) / Scale, h = t.Windows.Max(x => x.Bounds.Height) / Scale;
            if (t.Windows.Count > 1) { w *= 1.12; h *= 1.12; }
            size[t] = new Size(Math.Max(60, w), Math.Max(40, h));
        }
        // Order by position so the arrangement resembles the real desktop (top rows first, then left to right).
        var ordered = tiles.OrderBy(t => t.Primary.Bounds.Top + t.Primary.Bounds.Height / 2).ThenBy(t => t.Primary.Bounds.Left).ToList();
        double best = -1; List<List<Tile>> bestRows = new();
        int n = tiles.Count;
        for (int rows = 1; rows <= n; rows++)
        {
            int perRow = (int)Math.Ceiling(n / (double)rows);
            var rowList = new List<List<Tile>>();
            for (int r = 0; r < rows; r++)
            {
                var row = ordered.Skip(r * perRow).Take(perRow).OrderBy(t => t.Primary.Bounds.Left + t.Primary.Bounds.Width / 2).ToList();
                if (row.Count > 0) rowList.Add(row);
            }
            double rowH = (area.Height - RowGap * (rowList.Count - 1)) / rowList.Count;
            double scale = maxScale;
            foreach (var row in rowList)
            {
                double sumW = row.Sum(t => size[t].Width);
                double maxH = row.Max(t => size[t].Height);
                scale = Math.Min(scale, Math.Min((area.Width - TileGap * (row.Count - 1)) / sumW, rowH / maxH));
            }
            if (scale > best) { best = scale; bestRows = rowList; }
        }
        double totalH = bestRows.Sum(row => row.Max(t => size[t].Height) * best) + RowGap * (bestRows.Count - 1);
        double y = area.Y + (area.Height - totalH) / 2;
        foreach (var row in bestRows)
        {
            double rowH = row.Max(t => size[t].Height) * best;
            double rowW = row.Sum(t => size[t].Width) * best + TileGap * (row.Count - 1);
            double x = area.X + (area.Width - rowW) / 2;
            foreach (var t in row)
            {
                double w = size[t].Width * best, h = size[t].Height * best;
                t.Target = new Rect(x, y + (rowH - h) / 2, w, h);
                x += w + TileGap;
            }
            y += rowH + RowGap;
        }
    }

    /// <summary>Rectangle (px) of window i inside a tile rectangle; grouped windows fan out up and to the right.</summary>
    private static RECT Sub(Tile t, int i, RECT tile)
    {
        int n = t.Windows.Count;
        var w = t.Windows[i];
        if (n == 1) return tile;
        double fw = tile.Width * 0.88, fh = tile.Height * 0.88;
        double step = n > 1 ? (double)i / (n - 1) : 0;
        double ox = tile.Width * 0.12 * step, oy = tile.Height * 0.12 * (1 - step);
        var box = new RECT((int)(tile.Left + ox), (int)(tile.Top + oy), (int)(tile.Left + ox + fw), (int)(tile.Top + oy + fh));
        return Anim.Fit(box, w.Bounds.Width, w.Bounds.Height);
    }

    private static RECT Shrink(RECT r, double f)
    {
        int dw = (int)(r.Width * (1 - f) / 2), dh = (int)(r.Height * (1 - f) / 2);
        return new RECT(r.Left + dw, r.Top + dh, r.Right - dw, r.Bottom - dh);
    }

    private void RegisterThumbs(EnterKind kind)
    {
        // Register back-to-front so the stacking matches the real desktop while windows fly out of place.
        var z = new Dictionary<IntPtr, int>();
        for (int i = 0; i < _model.Windows.Count; i++) z[_model.Windows[i].Hwnd] = i;
        var order = new List<(Tile t, int i)>();
        foreach (var t in _tileList) { t.Thumbs = new List<Thumb?>(new Thumb?[t.Windows.Count]); for (int i = 0; i < t.Windows.Count; i++) order.Add((t, i)); }
        foreach (var (t, i) in order.OrderBy(o => o.t.IsMinimizedRow ? 1 : 0).ThenByDescending(o => z.GetValueOrDefault(o.t.Windows[o.i].Hwnd)))
        {
            var w = t.Windows[i];
            var th = Thumb.Create(Handle, w.Hwnd);
            t.Thumbs[i] = th;
            var target = Sub(t, i, Px(t.Target));
            if (th == null) { AddPlaceholder(t); continue; }
            if (kind == EnterKind.FromDesktop && !w.IsMinimized) th.Set(Rel(w.Bounds), 255);
            else if (kind == EnterKind.FromDesktop) th.Set(Shrink(target, 0.8), 0);
            else th.Set(target, 0);
        }
        if (DragHwnd != IntPtr.Zero)
        {
            _dragThumb = Thumb.Create(Handle, DragHwnd);
            User32.GetCursorPos(out var p);
            MoveDragThumb(p.X, p.Y);
        }
    }

    private void AddPlaceholder(Tile t)
    {
        if (t.Placeholder != null) return;
        var g = new Grid { Width = t.Target.Width, Height = t.Target.Height };
        g.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(60, 62, 70)), CornerRadius = new CornerRadius(8) });
        if (t.Primary.Icon != null) g.Children.Add(new Image { Source = t.Primary.Icon, Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Canvas.SetLeft(g, t.Target.X); Canvas.SetTop(g, t.Target.Y);
        t.Placeholder = g;
        _under.Children.Add(g);
    }

    // ------------------------------------------------------------------ animation helpers

    private AnimHandle AnimateThumbs(List<(Thumb th, RECT to, byte op)> targets, double ms, Func<double, double> ease, Action<double>? extra = null, Action? done = null,
        IReadOnlyList<double>? delays = null)
    {
        // Thumbnails move on the compositor-paced thread; WPF chrome (dim, bar) follows on the UI thread.
        var h = ThumbAnimator.Run(targets, ms, ease, done, Easing.OutCubic, delays);
        if (extra != null) h.Linked = Anim.Run(ms, Easing.Standard, extra);
        return h;
    }

    /// <summary>Start delays that let windows with a short way to go leave first: a soft cascade instead of a block move.</summary>
    private static List<double> Cascade(List<(Thumb th, RECT to, byte op)> targets, double totalMs)
    {
        var dist = targets.Select(t => (double)(Math.Abs(t.th.Current.Left - t.to.Left) + Math.Abs(t.th.Current.Top - t.to.Top))).ToList();
        var order = Enumerable.Range(0, targets.Count).OrderBy(i => dist[i]).ToList();
        var delays = new double[targets.Count];
        for (int r = 0; r < order.Count; r++) delays[order[r]] = order.Count > 1 ? totalMs * r / (order.Count - 1) : 0;
        return delays.ToList();
    }

    private List<(Thumb th, RECT to, byte op)> TileTargets(Tile t, RECT tilePx, byte opacity = 255)
    {
        var list = new List<(Thumb, RECT, byte)>();
        for (int i = 0; i < t.Windows.Count; i++)
            if (t.Thumbs[i] is { } th) list.Add((th, Sub(t, i, tilePx), opacity));
        return list;
    }

    private void SetTile(Tile t, RECT tilePx, byte opacity = 255)
    {
        for (int i = 0; i < t.Windows.Count; i++) t.Thumbs[i]?.Set(Sub(t, i, tilePx), opacity);
    }

    private void AnimateTile(Tile t, RECT tilePx, double ms, byte opacity = 255, Action? done = null)
    {
        t.Anim?.Cancel();
        t.Anim = AnimateThumbs(TileTargets(t, tilePx, opacity), ms, Easing.Bouncy, null, done);
    }

    /// <summary>Thumbnails stack in registration order; re-register a tile's thumbnails to put it on top.</summary>
    private void BringToFront(Tile t)
    {
        for (int i = t.Windows.Count - 1; i >= 0; i--)
        {
            var old = t.Thumbs[i];
            if (old == null) continue;
            var cur = old.Current; var op = old.Opacity; var crop = old.Crop;
            var fresh = Thumb.Create(Handle, t.Windows[i].Hwnd);
            if (fresh == null) continue;
            fresh.Crop = crop;
            fresh.Set(cur, op);
            old.Dispose();
            t.Thumbs[i] = fresh;
        }
    }

    /// <summary>0 = plain desktop, 1 = Mission Control chrome fully shown.</summary>
    private void SetChrome(double p)
    {
        _chrome = p;
        _dim.Opacity = DimMax * p;
        _barShift.Y = -(1 - p) * 70;
        _bar.Opacity = p;
        _header.Opacity = p;
        _under.Opacity = p;
    }

    public void BeginEnter(EnterKind kind)
    {
        var targets = new List<(Thumb, RECT, byte)>();
        foreach (var t in _tileList) targets.AddRange(TileTargets(t, Px(t.Target)));
        _mainAnim?.Cancel();
        if (kind == EnterKind.FromDesktop) _mainAnim = AnimateThumbs(targets, EnterMs, Easing.Glide, SetChrome, null, Cascade(targets, 60));
        else _mainAnim = AnimateThumbs(targets, 240, Easing.Standard);
        _poll.Start();
    }

    /// <summary>Windows fly back to where they really are while the chrome fades away.</summary>
    public void BeginExit(IntPtr activate, Action done)
    {
        _closing = true;
        _poll.Stop();
        SetHover(null);
        _dragThumb?.Dispose(); _dragThumb = null;
        _mainAnim?.Cancel();
        foreach (var t in _tileList) t.Anim?.Cancel();
        var front = _tileList.FirstOrDefault(t => t.Windows.Any(w => w.Hwnd == activate));
        if (front != null) BringToFront(front);
        var targets = new List<(Thumb, RECT, byte)>();
        foreach (var t in _tileList)
            for (int i = 0; i < t.Windows.Count; i++)
            {
                if (t.Thumbs[i] is not { } th) continue;
                var h = t.Windows[i].Hwnd;
                if (!User32.IsWindow(h) || User32.IsIconic(h) || Dwm.GetCloaked(h) != 0) targets.Add((th, Shrink(th.Current, 0.8), 0));
                else targets.Add((th, Rel(Dwm.GetFrameBounds(h)), 255));
            }
        double c0 = _chrome;
        _mainAnim = AnimateThumbs(targets, ExitMs, Easing.Settle, p => SetChrome(c0 * (1 - p)), done);
    }

    /// <summary>Hides the overlay immediately and releases the thumbnails.</summary>
    public void Finish()
    {
        _poll.Stop();
        _mainAnim?.Cancel();
        Conceal();
        DisposeThumbs();
        SetChrome(0);
        _hoverFrame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed;
        _closing = false; DragHwnd = IntPtr.Zero;
        _bar.ReleaseMouseCapture(); _hit.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------------ interaction: tiles

    private Tile? HitTile(Point p) => _tileList.LastOrDefault(t => t.Target.Contains(p));

    private void SetHover(Tile? t)
    {
        if (_hover == t) return;
        var prev = _hover;
        _hover = t;
        if (_quickLookOn && prev != null && prev == _quickLookTile) EndQuickLook();
        if (t == null || _closing) { _hoverFrame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(_hoverFrame, t.Target.X - 6); Canvas.SetTop(_hoverFrame, t.Target.Y - 6);
        _hoverFrame.Width = t.Target.Width + 12; _hoverFrame.Height = t.Target.Height + 12;
        _hoverFrame.Visibility = Visibility.Visible;
        ShowLabel(t);
        if (_quickLookOn) StartQuickLook(t);
    }

    private void ShowLabel(Tile t)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (t.Primary.Icon != null) sp.Children.Add(new Image { Source = t.Primary.Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) });
        string text = t.Windows.Count > 1 ? L.F("{0} – {1} windows", t.Primary.AppName, t.Windows.Count) : t.Primary.Title;
        sp.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, MaxWidth = 460, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI") });
        _label.Child = sp;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = _label.DesiredSize.Width, lh = _label.DesiredSize.Height;
        // Thumbnails are composited above WPF content, so the label sits just below (or above) the tile.
        double y = t.Target.Bottom + 12;
        if (y + lh > _bottomLimit - 4) y = t.Target.Y - lh - 12;
        _label.Margin = new Thickness(Math.Clamp(t.Target.X + (t.Target.Width - lw) / 2, 4, Math.Max(4, Width - lw - 4)), y, 0, 0);
    }

    private void Hit_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        var p = e.GetPosition(_hit);
        _pressed = HitTile(p); _pressPoint = p;
        _hit.CaptureMouse();
    }

    private void Hit_MouseMove(object sender, MouseEventArgs e)
    {
        if (_closing) return;
        var p = e.GetPosition(_hit);
        if (_dragging != null)
        {
            SetTile(_dragging, DragRect(_dragging, p));
            HighlightBarUnder(p);
            return;
        }
        if (_pressed != null && e.LeftButton == MouseButtonState.Pressed && _mode == MissionControlMode.Full && (Math.Abs(p.X - _pressPoint.X) > 6 || Math.Abs(p.Y - _pressPoint.Y) > 6))
        {
            StartTileDrag(_pressed, p);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) SetHover(HitTile(p));
    }

    private RECT DragRect(Tile t, Point p)
    {
        double w = Math.Max(90, t.Target.Width * 0.55), h = w * t.Target.Height / t.Target.Width;
        return Px(new Rect(p.X - w / 2, p.Y - h / 2, w, h));
    }

    private void StartTileDrag(Tile t, Point p)
    {
        SetHover(null);
        if (_quickLookOn) { _quickLookOn = false; _quickLookTile = null; }
        _dragging = t;
        t.Anim?.Cancel();
        BringToFront(t);
        AnimateTile(t, DragRect(t, p), 260);
        ExpandBar(true);
    }

    private void Hit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _hit.ReleaseMouseCapture();
        if (_closing) return;
        var p = e.GetPosition(_hit);
        if (_dragging != null)
        {
            var t = _dragging; _dragging = null;
            var target = BarItemAt(PointToScreenPx(p));
            HighlightBarUnder(null);
            bool valid = target != null && (target.IsAdd || (target.Space != null && target.Space.Id != _model.CurrentSpace));
            if (valid)
            {
                // Shrink into the space thumbnail, then really move the window(s).
                UpdateBarRects();
                var dest = Px(new Rect(target!.Rect.X + target.Rect.Width / 2 - 20, target.Rect.Y + BarPad + ThumbHeight / 2 - 12, 40, 24));
                AnimateTile(t, dest, 300, 0, () =>
                {
                    if (target.IsAdd)
                    {
                        var sp = _vd.Create();
                        if (sp != null) foreach (var w in t.Windows) _vd.MoveWindowToSpace(w.Hwnd, sp);
                    }
                    else
                    {
                        foreach (var w in t.Windows) { if (_model.Pinned.Contains(w.Hwnd)) _vd.PinWindow(w.Hwnd, false); _vd.MoveWindowToSpace(w.Hwnd, target.Space!); }
                    }
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _svc.Refresh());
                });
            }
            else
            {
                AnimateTile(t, Px(t.Target), 460);
                if (!_bar.IsMouseOver) ExpandBar(false);
            }
            _pressed = null;
            return;
        }
        var hit = HitTile(p);
        if (_pressed != null && hit == _pressed)
        {
            _svc.ActivateWindow(hit.Primary.Hwnd);
        }
        else if (_pressed == null && hit == null)
        {
            // Click on an empty desktop area (or the taskbar) exits Mission Control.
            _svc.Close();
        }
        _pressed = null;
    }

    private POINT PointToScreenPx(Point p) => new((int)(p.X * Scale) + Monitor.Bounds.Left, (int)(p.Y * Scale) + Monitor.Bounds.Top);

    // ------------------------------------------------------------------ interaction: spaces bar

    private void UpdateBarRects()
    {
        foreach (var it in _barList)
        {
            try
            {
                var tl = it.Element.TransformToAncestor(_root).Transform(new Point(0, 0));
                it.Rect = new Rect(tl, new Size(it.Element.ActualWidth, Math.Max(it.Element.ActualHeight, _bar.ActualHeight)));
            }
            catch { it.Rect = Rect.Empty; }
        }
    }

    private BarItem? BarItemAt(POINT screenPx)
    {
        if (_mode != MissionControlMode.Full || !Monitor.Bounds.Contains(screenPx.X, screenPx.Y)) return null;
        UpdateBarRects();
        var p = ToLocal(screenPx.X, screenPx.Y);
        if (p.Y > _bar.ActualHeight + 4) return null;
        return _barList.FirstOrDefault(b => b.Rect.Contains(p));
    }

    private void HighlightBarUnder(Point? p)
    {
        BarItem? target = p == null ? null : BarItemAt(PointToScreenPx(p.Value));
        foreach (var it in _barList)
        {
            bool hi = it == target;
            bool cur = it.Space?.Id == _model.CurrentSpace;
            it.Thumb.BorderBrush = hi ? new SolidColorBrush(Color.FromArgb(255, 110, 165, 255)) : (cur ? Brushes.White : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)));
            it.Thumb.BorderThickness = new Thickness(hi ? 3 : (cur ? 2.5 : 1));
        }
    }

    private void Bar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        var p = e.GetPosition(_root);
        UpdateBarRects();
        _barPressed = _barList.FirstOrDefault(b => b.Rect.Contains(p));
        _barDragStartX = p.X;
        if (_barPressed != null) { _bar.CaptureMouse(); e.Handled = true; }
    }

    private void Bar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_barPressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_root);
        if (_barDragging == null && !_barPressed.IsAdd && Math.Abs(p.X - _barDragStartX) > 10 && _model.Spaces.Count > 1)
        {
            _barDragging = _barPressed;
            _barDragging.Element.RenderTransform = new TranslateTransform();
            _barDragging.Element.Opacity = 0.75;
            Panel.SetZIndex(_barDragging.Element, 10);
        }
        if (_barDragging != null)
            ((TranslateTransform)_barDragging.Element.RenderTransform).X = p.X - _barDragStartX;
    }

    private void Bar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _bar.ReleaseMouseCapture();
        if (_closing) return;
        var p = e.GetPosition(_root);
        if (_barDragging != null)
        {
            var d = _barDragging; _barDragging = null;
            d.Element.RenderTransform = null; d.Element.Opacity = 1; Panel.SetZIndex(d.Element, 0);
            UpdateBarRects();
            int newIndex = _barList.Where(b => !b.IsAdd && b != d).Count(b => b.Rect.X + b.Rect.Width / 2 < p.X);
            if (d.Space != null && newIndex != d.Space.Index) { _vd.Move(d.Space, newIndex); Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _svc.Refresh()); }
            _barPressed = null;
            return;
        }
        var pressed = _barPressed; _barPressed = null;
        if (pressed == null) return;
        UpdateBarRects();
        if (!pressed.Rect.Contains(p)) return;
        if (pressed.IsAdd)
        {
            var sp = _vd.Create();
            if (sp != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _svc.Refresh());
            return;
        }
        if (pressed.Space == null) return;
        bool stay = IsAltDown();
        if (pressed.Space.Id == _model.CurrentSpace) { if (!stay) _svc.Close(); return; }
        _svc.SwitchSpace(pressed.Space, stay);
    }

    private void RemoveSpace(Space sp)
    {
        if (_model.Spaces.Count <= 1) return;
        _vd.Remove(sp);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _svc.Refresh());
    }

    private static bool IsAltDown() => User32.IsKeyDown(User32.VK_MENU);

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _svc.Close(); e.Handled = true; }
        else if (key == Key.Space) { if (!e.IsRepeat) ToggleQuickLook(); e.Handled = true; }
        else if (key is Key.LeftAlt or Key.RightAlt) { ShowAllCloseButtons(true); e.Handled = true; }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt) ShowAllCloseButtons(false);
    }

    private void ShowAllCloseButtons(bool show)
    {
        if (_mode != MissionControlMode.Full) return;
        if (show) ExpandBar(true);
        foreach (var it in _barList.Where(b => !b.IsAdd))
            it.Close.Visibility = (show || it.Element.IsMouseOver) && _barExpanded && _model.Spaces.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PollTick()
    {
        if (_closing || _mode != MissionControlMode.Full) return;
        bool alt = IsAltDown();
        if (alt != _altShown) { _altShown = alt; ShowAllCloseButtons(alt); }
    }

    // ---- Quick Look: Space enlarges the hovered window ----

    private void ToggleQuickLook()
    {
        if (_closing || _dragging != null) return;
        _quickLookOn = !_quickLookOn;
        if (_quickLookOn) { if (_hover != null) StartQuickLook(_hover); else _quickLookOn = false; }
        else EndQuickLook();
    }

    private void StartQuickLook(Tile t)
    {
        if (_quickLookTile == t) return;
        EndQuickLook();
        _quickLookTile = t;
        _hoverFrame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed;
        BringToFront(t);
        double maxW = Width * 0.82, maxH = (_bottomLimit) * 0.82;
        double s = Math.Min(maxW / t.Target.Width, maxH / t.Target.Height);
        s = Math.Min(s, Math.Max(1, t.Primary.Bounds.Width / Scale / t.Target.Width)); // never beyond real size
        double w = t.Target.Width * s, h = t.Target.Height * s;
        AnimateTile(t, Px(new Rect((Width - w) / 2, (_bottomLimit - h) / 2, w, h)), 440);
    }

    private void EndQuickLook()
    {
        var t = _quickLookTile; _quickLookTile = null;
        if (t != null) AnimateTile(t, Px(t.Target), 400);
    }

    // ------------------------------------------------------------------ drag mode (window dragged to the top edge)

    private void MoveDragThumb(int x, int y)
    {
        if (_dragThumb == null) return;
        var fb = Dwm.GetFrameBounds(DragHwnd);
        int w = (int)(fb.Width * 0.35), h = (int)(fb.Height * 0.35);
        int cx = x - Monitor.Bounds.Left, cy = y - Monitor.Bounds.Top;
        bool inside = Monitor.Bounds.Contains(x, y);
        _dragThumb.Set(new RECT(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), 230, inside);
    }

    public void ExternalMouse(uint msg, int x, int y)
    {
        if (DragHwnd == IntPtr.Zero || _closing) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (DragHwnd == IntPtr.Zero) return;
            MoveDragThumb(x, y);
            if (Monitor.Bounds.Contains(x, y)) HighlightBarUnder(ToLocal(x, y)); else HighlightBarUnder(null);
        });
    }

    public SpaceDropTarget? SpaceDropTarget(int x, int y)
    {
        var it = BarItemAt(new POINT(x, y));
        if (it == null) return null;
        return new SpaceDropTarget { Space = it.Space, IsAddButton = it.IsAdd };
    }
}
