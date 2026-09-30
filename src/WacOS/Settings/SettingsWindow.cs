using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WacOS.Desktop;
using WacOS.Native;

namespace WacOS.Settings;

/// <summary>System Settings look-alike: Desktop & Dock, Keyboard Shortcuts, Trackpad, Apps on Desktops, General.</summary>
public sealed class SettingsWindow : Window
{
    private readonly ListBox _nav = new();
    private readonly ScrollViewer _content = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 10, 28, 20) };
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(230, 230, 235));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(160, 160, 170));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(45, 45, 50));

    public SettingsWindow()
    {
        Title = L.T("WacOS Settings");
        Width = 860; Height = 640; MinWidth = 700; MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 36));
        Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/wacos.ico")); } catch { }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _nav.Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)); _nav.BorderThickness = new Thickness(0); _nav.Foreground = Fg; _nav.Padding = new Thickness(8, 12, 8, 12);
        BuildNav();
        _nav.SelectionChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        Grid.SetColumn(_nav, 0); grid.Children.Add(_nav);
        Grid.SetColumn(_content, 1); grid.Children.Add(_content);
        Content = grid;
        _nav.SelectedIndex = 0;
        ShowPage(0);
        SourceInitialized += (_, _) => { int dark = 1; Dwm.DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4); };
    }

    private void BuildNav()
    {
        int sel = Math.Max(0, _nav.SelectedIndex);
        _nav.Items.Clear();
        foreach (var n in new[] { L.T("Desktop & Dock"), L.T("Keyboard Shortcuts"), L.T("Trackpad"), L.T("Hot Corners"), L.T("Apps on Desktops"), L.T("General") })
            _nav.Items.Add(new ListBoxItem { Content = n, Padding = new Thickness(10, 8, 10, 8), FontSize = 14 });
        _nav.SelectedIndex = sel;
    }

    /// <summary>Re-creates every visible text after the language was changed.</summary>
    private void RebuildUi()
    {
        Title = L.T("WacOS Settings");
        BuildNav();
        ShowPage(_nav.SelectedIndex);
    }

    private void ShowPage(int idx)
    {
        if (idx < 0) return;
        _content.Content = idx switch
        {
            0 => DesktopAndDock(), 1 => Shortcuts(), 2 => Trackpad(), 3 => HotCorners(), 4 => AppsOnDesktops(), _ => General(),
        };
    }

    // ------------------------------------------------------------ helpers

    private static AppSettings S => App.Settings.Current;
    private static void Save() => App.Settings.Save();

    private static TextBlock Header(string t) => new() { Text = t, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 14), Foreground = Fg };
    private static TextBlock Section(string t) => new() { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8), Foreground = Fg };
    private static TextBlock Note(string t) => new() { Text = t, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), FontSize = 12 };

    private static Border CardPanel(params UIElement[] rows)
    {
        var sp = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            sp.Children.Add(rows[i]);
            if (i < rows.Length - 1) sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(62, 62, 68)), Margin = new Thickness(0, 6, 0, 6) });
        }
        return new Border { Background = Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), Child = sp };
    }

    private static Grid Row(string label, UIElement control, string? sub = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(new TextBlock { Text = label, Foreground = Fg, TextWrapping = TextWrapping.Wrap });
        if (sub != null) st.Children.Add(new TextBlock { Text = sub, Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(st, 0); g.Children.Add(st);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        Grid.SetColumn(control, 1); g.Children.Add(control);
        return g;
    }

    private static Grid Toggle(string label, Func<bool> get, Action<bool> set, string? sub = null)
    {
        var cb = new CheckBox { IsChecked = get(), Style = ToggleStyle() };
        cb.Checked += (_, _) => { set(true); Save(); };
        cb.Unchecked += (_, _) => { set(false); Save(); };
        return Row(label, cb, sub);
    }

    private static Style? _toggleStyle;
    private static Style ToggleStyle()
    {
        if (_toggleStyle != null) return _toggleStyle;
        // macOS-like switch drawn with a ControlTemplate.
        var style = new Style(typeof(CheckBox));
        var template = new ControlTemplate(typeof(CheckBox));
        var border = new FrameworkElementFactory(typeof(Border), "track");
        border.SetValue(Border.WidthProperty, 40.0); border.SetValue(Border.HeightProperty, 22.0); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(90, 90, 98)));
        var knob = new FrameworkElementFactory(typeof(Border), "knob");
        knob.SetValue(Border.WidthProperty, 18.0); knob.SetValue(Border.HeightProperty, 18.0); knob.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        knob.SetValue(Border.BackgroundProperty, Brushes.White); knob.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left); knob.SetValue(MarginProperty, new Thickness(2));
        border.AppendChild(knob);
        template.VisualTree = border;
        var trig = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
        trig.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(52, 120, 246)), "track"));
        trig.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "knob"));
        template.Triggers.Add(trig);
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        return _toggleStyle = style;
    }

    private static Style? _comboStyle;
    /// <summary>The stock ComboBox template ignores colours, so dark mode needs a template of its own.</summary>
    private static Style ComboStyle()
    {
        if (_comboStyle != null) return _comboStyle;
        const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>
  <Setter Property='Foreground' Value='#E6E6EB'/>
  <Setter Property='Height' Value='28'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='ItemContainerStyle'>
    <Setter.Value>
      <Style TargetType='ComboBoxItem'>
        <Setter Property='Foreground' Value='#E6E6EB'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ComboBoxItem'>
              <Border x:Name='bd' Background='Transparent' CornerRadius='5' Padding='10,5,10,5' Margin='4,1,4,1'>
                <ContentPresenter/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#3478F6'/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
    </Setter.Value>
  </Setter>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ComboBox'>
        <Grid>
          <ToggleButton x:Name='toggle' Focusable='False' ClickMode='Press'
                        IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
            <ToggleButton.Template>
              <ControlTemplate TargetType='ToggleButton'>
                <Border x:Name='box' Background='#3C3C44' BorderBrush='#55555F' BorderThickness='1' CornerRadius='6'>
                  <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,10,0' Data='M0,0 L4,4 L8,0' Stroke='#C8C8D0' StrokeThickness='1.6'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='Background' Value='#484852'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>
          <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' Margin='10,0,28,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>
          <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'
                 IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>
            <Border Background='#2D2D32' BorderBrush='#55555F' BorderThickness='1' CornerRadius='8' Margin='0,4,0,8' Padding='0,4,0,4'
                    MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
              <ScrollViewer VerticalScrollBarVisibility='Auto'><ItemsPresenter/></ScrollViewer>
            </Border>
          </Popup>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
        return _comboStyle = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static ComboBox Combo<T>(IEnumerable<(string label, T value)> items, T current, Action<T> set)
    {
        var cb = new ComboBox { MinWidth = 170, Style = ComboStyle() };
        var list = items.ToList();
        foreach (var (label, _) in list) cb.Items.Add(label);
        int idx = list.FindIndex(x => Equals(x.value, current));
        cb.SelectedIndex = idx < 0 ? 0 : idx;
        cb.SelectionChanged += (_, _) => { if (cb.SelectedIndex >= 0) { set(list[cb.SelectedIndex].value); Save(); } };
        return cb;
    }

    private static Button ShortcutButton(Func<KeyChord> get, Action<KeyChord> set)
    {
        var btn = new Button { Content = get().ToString(), MinWidth = 120, Padding = new Thickness(10, 4, 10, 4), Background = new SolidColorBrush(Color.FromRgb(60, 60, 68)), Foreground = Fg, BorderThickness = new Thickness(0) };
        btn.Click += (_, _) =>
        {
            btn.Content = L.T("Press keys… (Esc cancel, Backspace clear)");
            App.Hotkeys.Suspended = true;
            btn.Focus();
            void Handler(object s, KeyEventArgs e)
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
                btn.PreviewKeyDown -= Handler;
                App.Hotkeys.Suspended = false;
                if (key == Key.Escape) { btn.Content = get().ToString(); return; }
                var chord = key == Key.Back ? KeyChord.None : new KeyChord
                {
                    Key = KeyInterop.VirtualKeyFromKey(key),
                    Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                    Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0, Win = (Keyboard.Modifiers & ModifierKeys.Windows) != 0,
                };
                set(chord); Save();
                btn.Content = chord.ToString();
            }
            btn.PreviewKeyDown += Handler;
            btn.LostFocus += (_, _) => { btn.PreviewKeyDown -= Handler; App.Hotkeys.Suspended = false; btn.Content = get().ToString(); };
        };
        return btn;
    }

    // ------------------------------------------------------------ pages

    private UIElement DesktopAndDock()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("Desktop & Dock")));
        sp.Children.Add(Section(L.T("Stage Manager")));
        sp.Children.Add(CardPanel(
            Toggle(L.T("Stage Manager"), () => S.StageManagerEnabled, v => S.StageManagerEnabled = v, L.T("Organize apps and windows so that your desktop is clutter-free and you can stay focused.")),
            Toggle(L.T("Show recent apps in Stage Manager"), () => S.ShowRecentApps, v => S.ShowRecentApps = v, L.T("When off, move the pointer to the left edge of the screen to reveal them.")),
            Row(L.T("Show windows from an application"), Combo(new[] { (L.T("All at Once"), ShowWindowsMode.AllAtOnce), (L.T("One at a Time"), ShowWindowsMode.OneAtATime) }, S.ShowWindowsFromApplication, v => S.ShowWindowsFromApplication = v)),
            Toggle(L.T("Turn off Windows edge snapping while Stage Manager is on"), () => S.StageManagerBlocksEdgeSnap, v => S.StageManagerBlocksEdgeSnap = v, L.T("Dragging a window to the left edge stows it in the strip instead of snapping it to half the screen."))));

        sp.Children.Add(Section(L.T("Desktop & Stage Manager")));
        sp.Children.Add(CardPanel(
            Toggle(L.T("Show Items: On Desktop"), () => S.ShowItemsOnDesktop, v => S.ShowItemsOnDesktop = v),
            Toggle(L.T("Show Items: In Stage Manager"), () => S.ShowItemsInStageManager, v => S.ShowItemsInStageManager = v),
            Row(L.T("Click wallpaper to reveal desktop"), Combo(new[] { (L.T("Always"), ShowDesktopClick.Always), (L.T("Only in Stage Manager"), ShowDesktopClick.OnlyInStageManager) }, S.ShowDesktopOnWallpaperClick, v => S.ShowDesktopOnWallpaperClick = v))));

        sp.Children.Add(Section(L.T("Mission Control")));
        sp.Children.Add(CardPanel(
            Toggle(L.T("Automatically rearrange Spaces based on most recent use"), () => S.AutoRearrangeSpaces, v => S.AutoRearrangeSpaces = v),
            Toggle(L.T("When switching to an application, switch to a Space with open windows for the application"), () => S.SwitchToSpaceWithOpenWindows, v => S.SwitchToSpaceWithOpenWindows = v),
            Toggle(L.T("Group windows by application"), () => S.GroupWindowsByApplication, v => S.GroupWindowsByApplication = v),
            Toggle(L.T("Displays have separate Spaces"), () => S.DisplaysHaveSeparateSpaces, v => S.DisplaysHaveSeparateSpaces = v, L.T("Windows virtual desktops always span every display; Mission Control and Stage Manager are shown per display.")),
            Toggle(L.T("Drag windows to top of screen to enter Mission Control"), () => S.DragWindowsToTopToEnterMissionControl, v => S.DragWindowsToTopToEnterMissionControl = v),
            Toggle(L.T("Full-screen apps get their own Space"), () => S.FullscreenAppsGetOwnSpace, v => S.FullscreenAppsGetOwnSpace = v, L.T("Like macOS: a window that goes full screen moves to a new Space named after the app and returns when it leaves full screen."))));
        sp.Children.Add(Note(L.T("Shortcuts and Hot Corners are in their own pages.")));
        return sp;
    }

    private UIElement Shortcuts()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("Keyboard Shortcuts – Mission Control")));
        sp.Children.Add(CardPanel(
            Row(L.T("Mission Control"), ShortcutButton(() => S.MissionControl, v => S.MissionControl = v)),
            Row(L.T("Mission Control (alternate key)"), ShortcutButton(() => S.MissionControlAlt, v => S.MissionControlAlt = v)),
            Row(L.T("Application windows"), ShortcutButton(() => S.ApplicationWindows, v => S.ApplicationWindows = v)),
            Row(L.T("Show Desktop"), ShortcutButton(() => S.ShowDesktop, v => S.ShowDesktop = v)),
            Row(L.T("Turn Stage Manager on/off"), ShortcutButton(() => S.ToggleStageManager, v => S.ToggleStageManager = v)),
            Row(L.T("Move left a space"), ShortcutButton(() => S.MoveLeftASpace, v => S.MoveLeftASpace = v)),
            Row(L.T("Move right a space"), ShortcutButton(() => S.MoveRightASpace, v => S.MoveRightASpace = v)),
            Toggle(L.T("Switch to Desktop 1 … 10 with Ctrl+1 … Ctrl+0"), () => S.SwitchToDesktopNumberShortcuts, v => S.SwitchToDesktopNumberShortcuts = v),
            Row(L.T("Cycle windows of the current app (⌘` on macOS)"), ShortcutButton(() => S.CycleAppWindows, v => S.CycleAppWindows = v)),
            Toggle(L.T("Use Windows-style Win+Ctrl+←/→ instead of Ctrl+←/→"), () => S.UseWindowsStyleSpaceShortcuts, v => S.UseWindowsStyleSpaceShortcuts = v, L.T("Ctrl+arrow is word navigation in many Windows apps; enable this to avoid the conflict."))));
        return sp;
    }

    private UIElement Trackpad()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("Trackpad – More Gestures")));
        var fingers = new[] { (L.T("Off"), 0), (L.T("Swipe with three fingers"), 3), (L.T("Swipe with four fingers"), 4) };
        sp.Children.Add(CardPanel(
            Row(L.T("Swipe between full-screen apps / Spaces"), Combo(fingers, S.SwipeBetweenSpacesFingers, v => S.SwipeBetweenSpacesFingers = v)),
            Row(L.T("Mission Control"), Combo(fingers, S.MissionControlGestureFingers, v => S.MissionControlGestureFingers = v), L.T("Swipe up. Swipe down again to exit.")),
            Toggle(L.T("App Exposé"), () => S.AppExposeGesture, v => S.AppExposeGesture = v, L.T("Swipe down with the same number of fingers.")),
            Toggle(L.T("Show Desktop"), () => S.ShowDesktopGesture, v => S.ShowDesktopGesture = v, L.T("Spread with thumb and three fingers.")),
            Toggle(L.T("Launchpad (opens Start)"), () => S.LaunchpadGesture, v => S.LaunchpadGesture = v, L.T("Pinch with thumb and three fingers.")),
            Toggle(L.T("Natural swipe direction"), () => S.NaturalSwipeDirection, v => S.NaturalSwipeDirection = v, L.T("Swipe left to move to the Space on the right (content follows your fingers).")),
            Row(L.T("Sensitivity"), Slider(() => S.SwipeSensitivity, v => S.SwipeSensitivity = v)),
            Toggle(L.T("Turn off Windows' own three/four-finger swipes"), () => S.BlockWindowsTouchpadGestures, v => S.BlockWindowsTouchpadGestures = v, L.T("Prevents Windows from reacting to the same swipes. Your own settings are restored when WacOS quits.")),
            Toggle(L.T("Magic Mouse emulation"), () => S.MagicMouseEmulation, v => S.MagicMouseEmulation = v, L.T("Horizontal wheel tilt (or Shift+wheel) switches Spaces."))));
        sp.Children.Add(Note(App.Gestures.DeviceFound ? L.T("Precision Touchpad detected.") : L.T("No Precision Touchpad reports received yet – touch the touchpad with three fingers.")));
        var open = new Button { Content = L.T("Open Windows touchpad settings"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }); } catch { } };
        sp.Children.Add(open);
        return sp;
    }

    private static Slider Slider(Func<double> get, Action<double> set)
    {
        var s = new Slider { Minimum = 0.5, Maximum = 2.0, Value = get(), Width = 170, TickFrequency = 0.25, IsSnapToTickEnabled = true };
        s.ValueChanged += (_, _) => { set(s.Value); Save(); };
        return s;
    }

    private UIElement HotCorners()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("Hot Corners")));
        var actions = new[]
        {
            ("–", HotCornerAction.None), (L.T("Mission Control"), HotCornerAction.MissionControl), (L.T("Application Windows"), HotCornerAction.ApplicationWindows), (L.T("Desktop"), HotCornerAction.Desktop),
            (L.T("Notification Center"), HotCornerAction.NotificationCenter), (L.T("Launchpad (Start)"), HotCornerAction.Launchpad), (L.T("Quick Note"), HotCornerAction.QuickNote), (L.T("Lock Screen"), HotCornerAction.LockScreen), (L.T("Put Display to Sleep"), HotCornerAction.SleepDisplay),
        };
        sp.Children.Add(CardPanel(
            Row(L.T("Top left"), Combo(actions, S.HotCornerTopLeft, v => S.HotCornerTopLeft = v)),
            Row(L.T("Top right"), Combo(actions, S.HotCornerTopRight, v => S.HotCornerTopRight = v)),
            Row(L.T("Bottom left"), Combo(actions, S.HotCornerBottomLeft, v => S.HotCornerBottomLeft = v)),
            Row(L.T("Bottom right"), Combo(actions, S.HotCornerBottomRight, v => S.HotCornerBottomRight = v)),
            Row(L.T("Require modifier key"), Combo(new[] { (L.T("None"), ModifierKey.None), ("Ctrl", ModifierKey.Ctrl), ("Shift", ModifierKey.Shift), (L.T("Alt (Option)"), ModifierKey.Alt), (L.T("Win (Command)"), ModifierKey.Win) }, S.HotCornerModifier, v => S.HotCornerModifier = v))));
        return sp;
    }

    private UIElement AppsOnDesktops()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("Assign Apps to Desktops")));
        sp.Children.Add(Note(L.T("Equivalent of Dock → right-click an app → Options → Assign To. \"All Desktops\" shows the app's windows on every Space; \"This Desktop\" opens them on the chosen Space.")));
        var list = new StackPanel();
        void Refresh()
        {
            list.Children.Clear();
            var rows = new List<UIElement>();
            foreach (var a in S.AppAssignments.ToList())
            {
                var remove = new Button { Content = L.T("Remove"), Padding = new Thickness(8, 3, 8, 3) };
                var rule = a;
                remove.Click += (_, _) => { S.AppAssignments.Remove(rule); Save(); Refresh(); };
                string desc = a.Mode == AssignMode.AllDesktops ? L.T("All Desktops") : L.F("This Desktop ({0})", a.DesktopName);
                rows.Add(Row($"{a.AppName}  –  {desc}", remove, a.ExePath));
            }
            if (rows.Count == 0) rows.Add(Note(L.T("No assignments. Use the tray menu (right-click) → Assign \"App\" To … while the app is active, or add one below.")));
            list.Children.Add(CardPanel(rows.ToArray()));
        }
        Refresh();
        sp.Children.Add(list);
        sp.Children.Add(Section(L.T("Add for a running app")));
        var apps = WindowEnumerator.GetWindows(includeMinimized: true, includeOtherDesktops: true).GroupBy(w => w.AppKey).Select(g => g.First()).OrderBy(w => w.AppName).ToList();
        var combo = new ComboBox { MinWidth = 260, Style = ComboStyle() };
        foreach (var a in apps) combo.Items.Add(a.AppName);
        if (apps.Count > 0) combo.SelectedIndex = 0;
        var mode = new ComboBox { MinWidth = 150, Style = ComboStyle() }; mode.Items.Add(L.T("All Desktops")); mode.Items.Add(L.T("This Desktop")); mode.SelectedIndex = 0;
        var add = new Button { Content = L.T("Add"), Padding = new Thickness(12, 4, 12, 4) };
        add.Click += (_, _) => { if (combo.SelectedIndex >= 0) { App.Spaces.Assign(apps[combo.SelectedIndex], mode.SelectedIndex == 0 ? AssignMode.AllDesktops : AssignMode.ThisDesktop); Refresh(); } };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        line.Children.Add(combo); line.Children.Add(new Border { Width = 10 }); line.Children.Add(mode); line.Children.Add(new Border { Width = 10 }); line.Children.Add(add);
        sp.Children.Add(line);
        return sp;
    }

    private UIElement General()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header(L.T("General")));
        sp.Children.Add(CardPanel(
            Row(L.T("Language"), Combo(new[] { (L.T("System default"), "auto") }.Concat(L.Languages.Select(l => (l.name, l.code))), S.Language, v =>
            {
                S.Language = v;
                Dispatcher.BeginInvoke(RebuildUi);
            })),
            Toggle(L.T("Start WacOS when I sign in"), () => S.StartWithWindows, v => S.StartWithWindows = v),
            Toggle(L.T("Run as administrator"), () => S.RunAsAdministrator, v =>
            {
                S.RunAsAdministrator = v;
                // Switching it on takes effect right away; switching it off applies from the next start.
                if (v && !Elevation.IsElevated) Dispatcher.BeginInvoke(() => App.Current.RestartElevated());
            }, L.T("Lets WacOS move, resize and capture the windows of apps that run as administrator (Task Manager, game platforms, …). With it off those windows can still be stowed, but not repositioned.")),
            Toggle(L.T("Verbose logging"), () => S.VerboseLogging, v => S.VerboseLogging = v, Log.LogPath)));
        sp.Children.Add(Section(L.T("Status")));
        sp.Children.Add(CardPanel(
            Row(L.T("Virtual desktop API"), new TextBlock { Text = App.Desktops.IsAvailable ? L.T("Connected") : L.T("Unavailable"), Foreground = Fg }),
            Row(L.T("Windows build"), new TextBlock { Text = Environment.OSVersion.Version.ToString(), Foreground = Fg }),
            Row(L.T("Administrator mode"), new TextBlock { Text = Elevation.IsElevated ? L.T("On") : L.T("Off"), Foreground = Fg }),
            Row(L.T("Spaces"), new TextBlock { Text = App.Desktops.GetSpaces().Count.ToString(), Foreground = Fg })));
        sp.Children.Add(Note(L.T("WacOS lives in the system tray. Left-click the tray icon for Mission Control, right-click for the menu.")));
        var quit = new Button { Content = L.T("Quit WacOS"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        quit.Click += (_, _) => App.Current.Quit();
        sp.Children.Add(quit);
        return sp;
    }
}
