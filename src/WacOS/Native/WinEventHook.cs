namespace WacOS.Native;

/// <summary>Wraps SetWinEventHook. Callbacks arrive on the thread that owns the message loop (the UI thread).</summary>
public sealed class WinEventHook : IDisposable
{
    private readonly WinEventDelegate _proc;
    private IntPtr _hook;

    public event Action<uint, IntPtr, int, int>? Event;

    public WinEventHook(uint min, uint max)
    {
        _proc = OnEvent;
        _hook = User32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, User32.WINEVENT_OUTOFCONTEXT | User32.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        try { Event?.Invoke(eventType, hwnd, idObject, idChild); }
        catch (Exception ex) { Log.Error("WinEvent handler failed", ex); }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { User32.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}
