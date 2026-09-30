using System.Runtime.InteropServices;
using System.Windows.Threading;
using WacOS.Native;

namespace WacOS.VirtualDesktops;

/// <summary>A "Space" — one Windows virtual desktop.</summary>
public sealed record Space(Guid Id, int Index, string RawName, string WallpaperPath)
{
    /// <summary>macOS-style display name: user-provided name, otherwise "Desktop N".</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(RawName) ? L.F("Desktop {0}", Index + 1) : RawName;
}

/// <summary>
/// Thin, resilient wrapper over the internal virtual desktop COM API. All calls must happen on the UI (STA) thread.
/// Current-desktop changes are detected by polling (works on every build without the volatile notification interface).
/// </summary>
public sealed class VirtualDesktopService : IDisposable
{
    public const int MaxSpaces = 16;

    private IVirtualDesktopManagerInternal? _internal;
    private IVirtualDesktopManager? _manager;
    private IApplicationViewCollection? _views;
    private IVirtualDesktopPinnedApps? _pinned;
    private readonly DispatcherTimer _poll;
    private Guid _lastCurrent;
    private int _lastCount;
    private string _lastSignature = "";

    public event Action<Space?, Space>? CurrentChanged;
    public event Action? SpacesChanged;

    public bool IsAvailable => _internal != null;

    public VirtualDesktopService()
    {
        Init();
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private void Init()
    {
        try
        {
            var shellType = Type.GetTypeFromCLSID(VdGuids.CLSID_ImmersiveShell)!;
            var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
            Guid g = VdGuids.CLSID_VirtualDesktopManagerInternal, iid = typeof(IVirtualDesktopManagerInternal).GUID;
            _internal = (IVirtualDesktopManagerInternal)shell.QueryService(ref g, ref iid);
            // The public IVirtualDesktopManager is a registered COM class, not a shell service.
            _manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(VdGuids.CLSID_VirtualDesktopManager)!)!;
            g = typeof(IApplicationViewCollection).GUID; iid = g;
            _views = (IApplicationViewCollection)shell.QueryService(ref g, ref iid);
            g = VdGuids.CLSID_VirtualDesktopPinnedApps; iid = typeof(IVirtualDesktopPinnedApps).GUID;
            _pinned = (IVirtualDesktopPinnedApps)shell.QueryService(ref g, ref iid);
            _lastCurrent = _internal.GetCurrentDesktop().GetId();
            _lastCount = _internal.GetCount();
            Log.Info($"Virtual desktop API ready, {_lastCount} desktops");
        }
        catch (Exception ex)
        {
            Log.Error("Virtual desktop COM init failed (unsupported Windows build?)", ex);
            _internal = null;
        }
    }

    private T Guard<T>(Func<T> f, T fallback)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { if (_internal == null) Init(); if (_internal == null) return fallback; return f(); }
            catch (COMException ex) when ((uint)ex.HResult == 0x800706BA || (uint)ex.HResult == 0x800706BE || (uint)ex.HResult == 0x80010108)
            {
                Log.Warn("Shell COM connection lost, reinitialising"); _internal = null;
            }
            catch (Exception ex) { Log.Error("Virtual desktop call failed", ex); return fallback; }
        }
        return fallback;
    }

    private void Poll()
    {
        if (_internal == null) { Init(); if (_internal == null) return; }
        try
        {
            var cur = _internal.GetCurrentDesktop();
            var id = cur.GetId();
            int count = _internal.GetCount();
            if (id != _lastCurrent)
            {
                var old = _lastCurrent; _lastCurrent = id;
                var spaces = GetSpaces();
                var oldSpace = spaces.FirstOrDefault(s => s.Id == old);
                var newSpace = spaces.FirstOrDefault(s => s.Id == id) ?? new Space(id, 0, "", "");
                CurrentChanged?.Invoke(oldSpace, newSpace);
            }
            if (count != _lastCount) { _lastCount = count; SpacesChanged?.Invoke(); }
            else if (Environment.TickCount64 % 1000 < 130)
            {
                // Less frequent: check for renames / reorders.
                var sig = string.Join("|", GetSpaces().Select(s => s.Id.ToString("N") + s.RawName));
                if (sig != _lastSignature) { _lastSignature = sig; SpacesChanged?.Invoke(); }
            }
        }
        catch (COMException) { _internal = null; }
        catch (Exception ex) { Log.Error("Poll failed", ex); }
    }

    public void NotifySpacesChanged() => SpacesChanged?.Invoke();

    // ---- queries ----

    public IReadOnlyList<Space> GetSpaces() => Guard(() =>
    {
        var list = new List<Space>();
        _internal!.GetDesktops(out var arr);
        arr.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            var iid = VdGuids.IID_IVirtualDesktop;
            arr.GetAt(i, ref iid, out object o);
            var d = (IVirtualDesktop)o;
            string name = "", wp = "";
            try { name = HString.Take(d.GetName()); } catch { }
            try { wp = HString.Take(d.GetWallpaperPath()); } catch { }
            list.Add(new Space(d.GetId(), i, name, wp));
        }
        return (IReadOnlyList<Space>)list;
    }, Array.Empty<Space>());

    public Space? Current
    {
        get
        {
            var id = Guard(() => _internal!.GetCurrentDesktop().GetId(), Guid.Empty);
            return GetSpaces().FirstOrDefault(s => s.Id == id);
        }
    }

    public int CurrentIndex => Current?.Index ?? 0;

    /// <summary>Id of the current desktop as last seen by the poll (no COM call; at most ~120 ms behind a switch).</summary>
    public Guid CurrentId => _lastCurrent;

    public Guid GetWindowSpaceId(IntPtr hwnd) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) == 0 && view != null)
        {
            view.GetVirtualDesktopId(out var g);
            return g;
        }
        return _manager!.GetWindowDesktopId(hwnd);
    }, Guid.Empty);

    public bool IsWindowOnCurrentSpace(IntPtr hwnd) => Guard(() =>
    {
        if (IsWindowPinned(hwnd)) return true;
        return _manager!.IsWindowOnCurrentVirtualDesktop(hwnd);
    }, true);

    // ---- switching ----

    public void SwitchTo(Space space, bool animated = true) => SwitchTo(space.Id, animated);

    /// <summary>When WacOS itself last asked for a switch, and whether that was because an app was activated.</summary>
    public long LastOwnSwitchTick { get; private set; }
    public bool LastOwnSwitchWasActivation { get; private set; }

    public void SwitchTo(Guid id, bool animated = true, bool byActivation = false) => Guard(() =>
    {
        LastOwnSwitchTick = Environment.TickCount64; LastOwnSwitchWasActivation = byActivation;
        var d = _internal!.FindDesktop(ref id);
        if (animated) { try { _internal.SwitchDesktopWithAnimation(d); } catch { _internal.SwitchDesktop(d); } }
        else _internal.SwitchDesktop(d);
        return true;
    }, false);

    /// <summary>Switch left/right without wrap‑around, like macOS. Returns false when at the end.</summary>
    public bool SwitchAdjacent(int direction)
    {
        var spaces = GetSpaces();
        int idx = CurrentIndex + direction;
        if (idx < 0 || idx >= spaces.Count) return false;
        SwitchTo(spaces[idx]);
        return true;
    }

    public bool SwitchToIndex(int index)
    {
        var spaces = GetSpaces();
        if (index < 0 || index >= spaces.Count) return false;
        if (spaces[index].Id == _lastCurrent) return true;
        SwitchTo(spaces[index]);
        return true;
    }

    /// <summary>Switch and take the foreground window along (used while the user drags a window).</summary>
    public void SwitchAndMoveForeground(Space target) => Guard(() =>
    {
        var id = target.Id;
        var d = _internal!.FindDesktop(ref id);
        _internal.SwitchDesktopAndMoveForegroundView(d);
        return true;
    }, false);

    // ---- editing ----

    public Space? Create()
    {
        if (GetSpaces().Count >= MaxSpaces) { Log.Warn("Maximum of 16 spaces reached"); return null; }
        var id = Guard(() => _internal!.CreateDesktop().GetId(), Guid.Empty);
        if (id == Guid.Empty) return null;
        SpacesChanged?.Invoke();
        return GetSpaces().FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Removes a space; its windows go to the space on its left (Desktop 1 if it is the first).</summary>
    public bool Remove(Space space, Space? moveWindowsTo = null)
    {
        var spaces = GetSpaces();
        if (spaces.Count <= 1) return false;
        var fallback = moveWindowsTo != null && moveWindowsTo.Id != space.Id && spaces.Any(x => x.Id == moveWindowsTo.Id)
            ? moveWindowsTo : space.Index > 0 ? spaces[space.Index - 1] : spaces[1];
        var ok = Guard(() =>
        {
            Guid a = space.Id, b = fallback.Id;
            _internal!.RemoveDesktop(_internal.FindDesktop(ref a), _internal.FindDesktop(ref b));
            return true;
        }, false);
        if (ok) SpacesChanged?.Invoke();
        return ok;
    }

    public void Move(Space space, int newIndex)
    {
        Guard(() => { Guid a = space.Id; _internal!.MoveDesktop(_internal.FindDesktop(ref a), newIndex); return true; }, false);
        SpacesChanged?.Invoke();
    }

    public void Rename(Space space, string name)
    {
        Guard(() => { Guid a = space.Id; var h = HString.Create(name); try { _internal!.SetDesktopName(_internal.FindDesktop(ref a), h); } finally { HString.Free(h); } return true; }, false);
        SpacesChanged?.Invoke();
    }

    public void SetWallpaper(Space space, string path)
    {
        Guard(() => { Guid a = space.Id; var h = HString.Create(path); try { _internal!.SetDesktopWallpaper(_internal.FindDesktop(ref a), h); } finally { HString.Free(h); } return true; }, false);
        SpacesChanged?.Invoke();
    }

    public bool MoveWindowToSpace(IntPtr hwnd, Space target) => Guard(() =>
    {
        Guid id = target.Id;
        var d = _internal!.FindDesktop(ref id);
        if (_views!.GetViewForHwnd(hwnd, out var view) == 0 && view != null)
        {
            _internal.MoveViewToDesktop(view, d);
            return true;
        }
        _manager!.MoveWindowToDesktop(hwnd, ref id);
        return true;
    }, false);

    // ---- pinning ("All Desktops") ----

    public bool IsWindowPinned(IntPtr hwnd) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) != 0 || view == null) return false;
        return _pinned!.IsViewPinned(view);
    }, false);

    public void PinWindow(IntPtr hwnd, bool pin) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) != 0 || view == null) return false;
        if (pin) _pinned!.PinView(view); else _pinned!.UnpinView(view);
        return true;
    }, false);

    /// <summary>Raw IApplicationView.SetCloak call (experimental). Returns the HRESULT, or -1 when the view is missing.</summary>
    public int SetViewCloak(IntPtr hwnd, int cloakType, int flag) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) != 0 || view == null) return -1;
        return view.SetCloak(cloakType, flag);
    }, -2);

    /// <summary>Asks the shell to switch to a window, exactly like a taskbar click (no foreground-lock restrictions).</summary>
    public bool ActivateView(IntPtr hwnd) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) != 0 || view == null) return false;
        return view.SwitchTo() == 0;
    }, false);

    public string? GetAppUserModelId(IntPtr hwnd) => Guard(() =>
    {
        if (_views!.GetViewForHwnd(hwnd, out var view) != 0 || view == null) return null;
        view.GetAppUserModelId(out var id);
        return id;
    }, null);

    public bool IsAppPinned(string appId) => Guard(() => _pinned!.IsAppIdPinned(appId), false);
    public void PinApp(string appId, bool pin) => Guard(() => { if (pin) _pinned!.PinAppID(appId); else _pinned!.UnpinAppID(appId); return true; }, false);

    public void Dispose() => _poll.Stop();
}
