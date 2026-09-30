using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WacOS.Native;

namespace WacOS.Settings;

public enum HotCornerAction
{
    None, MissionControl, ApplicationWindows, Desktop, NotificationCenter, Launchpad, QuickNote, LockScreen, SleepDisplay
}

public enum ModifierKey { None, Ctrl, Shift, Alt, Win }
public enum ShowWindowsMode { AllAtOnce, OneAtATime }
public enum ShowDesktopClick { Always, OnlyInStageManager }
public enum AssignMode { None, AllDesktops, ThisDesktop }

/// <summary>A keyboard shortcut. Key is a Win32 virtual-key code; 0 = unassigned.</summary>
public sealed class KeyChord
{
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }

    [JsonIgnore] public bool IsAssigned => Key != 0;

    public static KeyChord Of(int key, bool ctrl = false, bool shift = false, bool alt = false, bool win = false)
        => new() { Key = key, Ctrl = ctrl, Shift = shift, Alt = alt, Win = win };
    public static KeyChord None => new();

    public bool Matches(int vk, bool ctrl, bool shift, bool alt, bool win)
        => Key != 0 && vk == Key && ctrl == Ctrl && shift == Shift && alt == Alt && win == Win;

    public override string ToString()
    {
        if (Key == 0) return "–";
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static string KeyName(int vk) => vk switch
    {
        User32.VK_UP => "↑", User32.VK_DOWN => "↓", User32.VK_LEFT => "←", User32.VK_RIGHT => "→",
        User32.VK_ESCAPE => "Esc", User32.VK_SPACE => "Space", User32.VK_TAB => "Tab", User32.VK_RETURN => "Enter",
        User32.VK_OEM_3 => "`",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => ((System.Windows.Forms.Keys)vk).ToString(),
    };
}

public sealed class AppAssignment
{
    public string ExePath { get; set; } = "";
    public string AppName { get; set; } = "";
    public AssignMode Mode { get; set; }
    public Guid DesktopId { get; set; }
    public string DesktopName { get; set; } = "";
}

public sealed class AppSettings
{
    // ---- Desktop & Dock → Mission Control ----
    public bool AutoRearrangeSpaces { get; set; } = false;
    public bool SwitchToSpaceWithOpenWindows { get; set; } = true;
    public bool GroupWindowsByApplication { get; set; } = false;
    public bool DisplaysHaveSeparateSpaces { get; set; } = true;
    public bool DragWindowsToTopToEnterMissionControl { get; set; } = true;
    public bool FullscreenAppsGetOwnSpace { get; set; } = true;

    // ---- Keyboard → Shortcuts → Mission Control ----
    public KeyChord MissionControl { get; set; } = KeyChord.Of(User32.VK_UP, ctrl: true);
    public KeyChord MissionControlAlt { get; set; } = KeyChord.Of(User32.VK_F3);
    public KeyChord ApplicationWindows { get; set; } = KeyChord.Of(User32.VK_DOWN, ctrl: true);
    public KeyChord ShowDesktop { get; set; } = KeyChord.Of(User32.VK_F11);
    public KeyChord MoveLeftASpace { get; set; } = KeyChord.Of(User32.VK_LEFT, ctrl: true);
    public KeyChord MoveRightASpace { get; set; } = KeyChord.Of(User32.VK_RIGHT, ctrl: true);
    public bool SwitchToDesktopNumberShortcuts { get; set; } = false;   // Ctrl+1 … Ctrl+9, Ctrl+0 = Desktop 10
    public KeyChord ToggleStageManager { get; set; } = KeyChord.None;
    public KeyChord CycleAppWindows { get; set; } = KeyChord.Of(User32.VK_OEM_3, alt: true); // Cmd+` on macOS
    /// <summary>Use Windows' native Win+Ctrl+←/→ instead of Ctrl+←/→ (avoids the word-navigation conflict).</summary>
    public bool UseWindowsStyleSpaceShortcuts { get; set; } = false;

    // ---- Hot Corners ----
    public HotCornerAction HotCornerTopLeft { get; set; } = HotCornerAction.None;
    public HotCornerAction HotCornerTopRight { get; set; } = HotCornerAction.None;
    public HotCornerAction HotCornerBottomLeft { get; set; } = HotCornerAction.None;
    public HotCornerAction HotCornerBottomRight { get; set; } = HotCornerAction.None;
    public ModifierKey HotCornerModifier { get; set; } = ModifierKey.None;

    // ---- Trackpad → More Gestures ----
    public int SwipeBetweenSpacesFingers { get; set; } = 3;     // 0 = off, 3 or 4
    public int MissionControlGestureFingers { get; set; } = 3;  // 0 = off, 3 or 4 (also App Exposé down‑swipe)
    public bool AppExposeGesture { get; set; } = true;
    public bool ShowDesktopGesture { get; set; } = true;        // spread with thumb and three fingers
    public bool LaunchpadGesture { get; set; } = false;         // pinch → Start menu
    public bool NaturalSwipeDirection { get; set; } = true;
    /// <summary>Set Windows' own three/four-finger swipes to "Nothing" while WacOS handles those gestures.</summary>
    public bool BlockWindowsTouchpadGestures { get; set; } = true;     // swipe left → space on the right (macOS default)
    public bool MagicMouseEmulation { get; set; } = false;      // horizontal wheel / Shift+wheel switches spaces
    public double SwipeSensitivity { get; set; } = 1.0;

    // ---- Stage Manager ----
    public bool StageManagerEnabled { get; set; } = false;
    public bool ShowRecentApps { get; set; } = true;
    public ShowWindowsMode ShowWindowsFromApplication { get; set; } = ShowWindowsMode.AllAtOnce;
    public bool ShowItemsOnDesktop { get; set; } = true;
    public bool ShowItemsInStageManager { get; set; } = false;
    public ShowDesktopClick ShowDesktopOnWallpaperClick { get; set; } = ShowDesktopClick.OnlyInStageManager;
    /// <summary>While Stage Manager is on, dragging a window to a screen edge does not trigger Windows' snap layouts.</summary>
    public bool StageManagerBlocksEdgeSnap { get; set; } = true;

    // ---- App assignments (Dock → Options → Assign To) ----
    public List<AppAssignment> AppAssignments { get; set; } = new();

    // ---- Misc ----
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Run elevated so the windows of elevated apps can be managed like any other window.</summary>
    public bool RunAsAdministrator { get; set; } = false;
    public bool VerboseLogging { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;
    /// <summary>UI language: "auto" (follow the system), "en", "zh-Hans", "zh-Hant" or "ja".</summary>
    public string Language { get; set; } = "auto";
}

public sealed class SettingsStore
{
    public static readonly string Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WacOS");
    public static readonly string FilePath = Path.Combine(Directory, "settings.json");
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), _json) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Error("Failed to load settings", ex); Current = new AppSettings(); }
        Log.Verbose = Log.Verbose || Current.VerboseLogging;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, _json));
        }
        catch (Exception ex) { Log.Error("Failed to save settings", ex); }
        Log.Verbose = Log.Verbose || Current.VerboseLogging;
        Changed?.Invoke();
    }

    public void Update(Action<AppSettings> edit) { edit(Current); Save(); }
}
