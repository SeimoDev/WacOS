using System.Runtime.InteropServices;

namespace WacOS.Native;

public sealed class KeyEventArgs2
{
    public int VirtualKey;
    public bool IsDown;
    public bool Injected;
    public bool Ctrl, Shift, Alt, Win;
    /// <summary>Set to true to swallow the key so no other application sees it.</summary>
    public bool Handled;
}

public sealed class MouseEventArgs2
{
    public uint Message;
    public int X, Y;
    public int WheelDelta;
    public bool Injected;
    public bool Handled;
}

/// <summary>
/// Low-level keyboard and mouse hooks running on a dedicated thread with its own message loop, so a busy UI thread
/// never makes the hook callbacks time out (Windows silently removes slow low-level hooks).
/// Handlers run on the hook thread and must be quick; dispatch real work to the UI thread.
/// </summary>
public sealed class LowLevelHooks : IDisposable
{
    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;
    private IntPtr _kbHook, _mouseHook;
    private readonly Thread _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new();

    public event Action<KeyEventArgs2>? Key;
    public event Action<MouseEventArgs2>? Mouse;

    public LowLevelHooks()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "WacOS.LowLevelHooks", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        _threadId = Kernel32.GetCurrentThreadId();
        var mod = Kernel32.GetModuleHandle(null);
        _kbHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _kbProc, mod, 0);
        _mouseHook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _mouseProc, mod, 0);
        if (_kbHook == IntPtr.Zero) Log.Warn("Keyboard hook failed: " + Marshal.GetLastWin32Error());
        if (_mouseHook == IntPtr.Zero) Log.Warn("Mouse hook failed: " + Marshal.GetLastWin32Error());
        _ready.Set();
        while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == 0x0012 /* WM_QUIT */) break;
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
        if (_kbHook != IntPtr.Zero) { User32.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { User32.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == User32.HC_ACTION && Key != null)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam.ToInt64();
            var e = new KeyEventArgs2
            {
                VirtualKey = (int)k.vkCode,
                IsDown = msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN,
                Injected = k.dwExtraInfo == User32.InjectedMarker, // only WacOS' own synthetic input is ignored
                Ctrl = User32.IsKeyDown(User32.VK_CONTROL),
                Shift = User32.IsKeyDown(User32.VK_SHIFT),
                Alt = User32.IsKeyDown(User32.VK_MENU),
                Win = User32.IsKeyDown(User32.VK_LWIN) || User32.IsKeyDown(User32.VK_RWIN),
            };
            try { Key(e); } catch (Exception ex) { Log.Error("Key hook handler", ex); }
            if (e.Handled) return new IntPtr(1);
        }
        return User32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == User32.HC_ACTION && Mouse != null)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam.ToInt64();
            var e = new MouseEventArgs2
            {
                Message = msg,
                X = m.pt.X,
                Y = m.pt.Y,
                WheelDelta = (msg == User32.WM_MOUSEWHEEL || msg == User32.WM_MOUSEHWHEEL) ? (short)(m.mouseData >> 16) : 0,
                Injected = m.dwExtraInfo == User32.InjectedMarker,
            };
            try { Mouse(e); } catch (Exception ex) { Log.Error("Mouse hook handler", ex); }
            if (e.Handled) return new IntPtr(1);
        }
        return User32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) User32.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}
