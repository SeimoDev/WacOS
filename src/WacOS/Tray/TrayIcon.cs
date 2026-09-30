using System.Drawing;
using System.Windows.Forms;
using WacOS.Desktop;
using WacOS.Native;
using WacOS.Settings;

namespace WacOS.Tray;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    public TrayIcon()
    {
        Icon? ico = null;
        try { ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _icon = new NotifyIcon { Icon = ico ?? SystemIcons.Application, Text = "WacOS – Spaces, Mission Control & Stage Manager", Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.MissionControl.Toggle(); };
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        _menu.Items.Clear();
        var s = App.Settings.Current;
        _menu.Items.Add("Mission Control", null, (_, _) => App.MissionControl.Toggle());
        _menu.Items.Add("Application Windows", null, (_, _) => App.MissionControl.ToggleAppExpose());
        _menu.Items.Add("Show Desktop", null, (_, _) => App.ShowDesktop.Toggle());
        _menu.Items.Add(new ToolStripSeparator());
        var sm = new ToolStripMenuItem("Stage Manager") { Checked = App.StageManager.IsEnabled, CheckOnClick = true };
        sm.Click += (_, _) => App.StageManager.SetEnabled(!App.StageManager.IsEnabled);
        _menu.Items.Add(sm);
        _menu.Items.Add(new ToolStripSeparator());

        // Spaces submenu
        var spacesMenu = new ToolStripMenuItem("Spaces");
        var spaces = App.Desktops.GetSpaces();
        var cur = App.Desktops.Current;
        foreach (var sp in spaces)
        {
            var item = new ToolStripMenuItem(sp.DisplayName) { Checked = sp.Id == cur?.Id };
            var target = sp;
            item.Click += (_, _) => App.Desktops.SwitchTo(target);
            spacesMenu.DropDownItems.Add(item);
        }
        spacesMenu.DropDownItems.Add(new ToolStripSeparator());
        spacesMenu.DropDownItems.Add("New Desktop", null, (_, _) => App.Desktops.Create());
        var remove = new ToolStripMenuItem("Remove Current Desktop") { Enabled = spaces.Count > 1 };
        remove.Click += (_, _) => { if (App.Desktops.Current is { } c) App.Desktops.Remove(c); };
        spacesMenu.DropDownItems.Add(remove);
        spacesMenu.DropDownItems.Add("Rename Current Desktop…", null, (_, _) => RenameCurrent());
        spacesMenu.DropDownItems.Add("Choose Wallpaper for Current Desktop…", null, (_, _) => ChooseWallpaper());
        _menu.Items.Add(spacesMenu);

        // Assign the active app (Dock → Options → Assign To)
        var fg = WindowEnumerator.Describe(User32.GetForegroundWindow()) ?? WindowEnumerator.GetWindows(includeMinimized: false).FirstOrDefault();
        if (fg != null)
        {
            var assign = new ToolStripMenuItem($"Assign \"{fg.AppName}\" To");
            var rule = App.Spaces.FindAssignment(fg.ExePath);
            foreach (var (label, mode) in new[] { ("All Desktops", AssignMode.AllDesktops), ("This Desktop", AssignMode.ThisDesktop), ("None", AssignMode.None) })
            {
                var m = mode; var it = new ToolStripMenuItem(label) { Checked = (rule?.Mode ?? AssignMode.None) == mode };
                it.Click += (_, _) => App.Spaces.Assign(fg, m);
                assign.DropDownItems.Add(it);
            }
            _menu.Items.Add(assign);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings…", null, (_, _) => App.Current.OpenSettings());
        _menu.Items.Add("Open Log", null, (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.LogPath) { UseShellExecute = true }); } catch { } });
        _menu.Items.Add("Quit WacOS", null, (_, _) => App.Current.Quit());
    }

    private static void RenameCurrent()
    {
        var cur = App.Desktops.Current; if (cur == null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox("Name for this desktop:", "Rename Desktop", cur.DisplayName);
        if (!string.IsNullOrWhiteSpace(name)) App.Desktops.Rename(cur, name.Trim());
    }

    private static void ChooseWallpaper()
    {
        var cur = App.Desktops.Current; if (cur == null) return;
        using var dlg = new OpenFileDialog { Title = "Wallpaper for " + cur.DisplayName, Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.jxr|All files|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK) App.Desktops.SetWallpaper(cur, dlg.FileName);
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
