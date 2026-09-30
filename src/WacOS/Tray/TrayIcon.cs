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
        _icon = new NotifyIcon { Icon = ico ?? SystemIcons.Application, Text = L.T("WacOS – Spaces, Mission Control & Stage Manager"), Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.MissionControl.Toggle(); };
        _menu.Opening += (_, _) => Rebuild();
        App.Settings.Changed += () => { try { _icon.Text = L.T("WacOS – Spaces, Mission Control & Stage Manager"); } catch { } };
        Rebuild();
    }

    private void Rebuild()
    {
        _menu.Items.Clear();
        _icon.Text = L.T("WacOS – Spaces, Mission Control & Stage Manager");
        var s = App.Settings.Current;
        _menu.Items.Add(L.T("Mission Control"), null, (_, _) => App.MissionControl.Toggle());
        _menu.Items.Add(L.T("Application Windows"), null, (_, _) => App.MissionControl.ToggleAppExpose());
        _menu.Items.Add(L.T("Show Desktop"), null, (_, _) => App.ShowDesktop.Toggle());
        _menu.Items.Add(new ToolStripSeparator());
        var sm = new ToolStripMenuItem(L.T("Stage Manager")) { Checked = App.StageManager.IsEnabled, CheckOnClick = true };
        sm.Click += (_, _) => App.StageManager.SetEnabled(!App.StageManager.IsEnabled);
        _menu.Items.Add(sm);
        _menu.Items.Add(new ToolStripSeparator());

        // Spaces submenu
        var spacesMenu = new ToolStripMenuItem(L.T("Spaces"));
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
        spacesMenu.DropDownItems.Add(L.T("New Desktop"), null, (_, _) => App.Desktops.Create());
        var remove = new ToolStripMenuItem(L.T("Remove Current Desktop")) { Enabled = spaces.Count > 1 };
        remove.Click += (_, _) => { if (App.Desktops.Current is { } c) App.Desktops.Remove(c); };
        spacesMenu.DropDownItems.Add(remove);
        spacesMenu.DropDownItems.Add(L.T("Rename Current Desktop…"), null, (_, _) => RenameCurrent());
        spacesMenu.DropDownItems.Add(L.T("Choose Wallpaper for Current Desktop…"), null, (_, _) => ChooseWallpaper());
        _menu.Items.Add(spacesMenu);

        // Assign the active app (Dock → Options → Assign To)
        var fg = WindowEnumerator.Describe(User32.GetForegroundWindow()) ?? WindowEnumerator.GetWindows(includeMinimized: false).FirstOrDefault();
        if (fg != null)
        {
            var assign = new ToolStripMenuItem(L.F("Assign \"{0}\" To", fg.AppName));
            var rule = App.Spaces.FindAssignment(fg.ExePath);
            foreach (var (label, mode) in new[] { (L.T("All Desktops"), AssignMode.AllDesktops), (L.T("This Desktop"), AssignMode.ThisDesktop), (L.T("None"), AssignMode.None) })
            {
                var m = mode; var it = new ToolStripMenuItem(label) { Checked = (rule?.Mode ?? AssignMode.None) == mode };
                it.Click += (_, _) => App.Spaces.Assign(fg, m);
                assign.DropDownItems.Add(it);
            }
            _menu.Items.Add(assign);
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.T("Settings…"), null, (_, _) => App.Current.OpenSettings());
        _menu.Items.Add(L.T("Open Log"), null, (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.LogPath) { UseShellExecute = true }); } catch { } });
        _menu.Items.Add(L.T("Quit WacOS"), null, (_, _) => App.Current.Quit());
    }

    private static void RenameCurrent()
    {
        var cur = App.Desktops.Current; if (cur == null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox(L.T("Name for this desktop:"), L.T("Rename Desktop"), cur.DisplayName);
        if (!string.IsNullOrWhiteSpace(name)) App.Desktops.Rename(cur, name.Trim());
    }

    private static void ChooseWallpaper()
    {
        var cur = App.Desktops.Current; if (cur == null) return;
        using var dlg = new OpenFileDialog { Title = L.F("Wallpaper for {0}", cur.DisplayName), Filter = L.T("Images") + "|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.jxr|" + L.T("All files") + "|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK) App.Desktops.SetWallpaper(cur, dlg.FileName);
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }
}
