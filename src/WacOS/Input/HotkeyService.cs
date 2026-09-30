using WacOS.Native;
using WacOS.Settings;

namespace WacOS.Input;

/// <summary>
/// Global shortcuts driven by the low-level keyboard hook, so chords like Ctrl+↑ can be swallowed system-wide.
/// Matching happens on the hook thread; actions are dispatched to the UI thread.
/// </summary>
public sealed class HotkeyService
{
    private sealed record Binding(string Name, Func<KeyChord?> Chord, Action Action, bool AllowRepeat);
    private readonly List<Binding> _bindings = new();
    private readonly Dictionary<string, long> _lastFired = new();
    /// <summary>Set by features to temporarily suppress global shortcuts (e.g. while recording a shortcut in Settings).</summary>
    public volatile bool Suspended;

    public HotkeyService(LowLevelHooks hooks) => hooks.Key += OnKey;

    public void Register(string name, Func<KeyChord?> chord, Action action, bool allowRepeat = false)
    {
        lock (_bindings) _bindings.Add(new Binding(name, chord, action, allowRepeat));
    }

    private static bool IsModifier(int vk) => vk is User32.VK_SHIFT or User32.VK_CONTROL or User32.VK_MENU or User32.VK_LWIN or User32.VK_RWIN
        or User32.VK_LSHIFT or User32.VK_RSHIFT or User32.VK_LCONTROL or User32.VK_RCONTROL or User32.VK_LMENU or User32.VK_RMENU;

    private void OnKey(KeyEventArgs2 e)
    {
        if (Suspended || e.Injected || IsModifier(e.VirtualKey)) return;
        Binding[] bindings;
        lock (_bindings) bindings = _bindings.ToArray();
        foreach (var b in bindings)
        {
            KeyChord? chord;
            try { chord = b.Chord(); } catch { continue; }
            if (chord == null || !chord.Matches(e.VirtualKey, e.Ctrl, e.Shift, e.Alt, e.Win)) continue;
            e.Handled = true; // swallow both key down and key up of a bound chord
            if (!e.IsDown) return;
            long now = Environment.TickCount64;
            lock (_lastFired)
            {
                if (_lastFired.TryGetValue(b.Name, out var last) && now - last < (b.AllowRepeat ? 200 : 350)) return;
                _lastFired[b.Name] = now;
            }
            var action = b.Action; var name = b.Name;
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                try { action(); } catch (Exception ex) { Log.Error($"Shortcut {name} failed", ex); }
            });
            return;
        }
    }
}
