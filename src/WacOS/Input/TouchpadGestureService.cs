using System.Runtime.InteropServices;
using WacOS.Native;

namespace WacOS.Input;

public enum GestureKind { SwipeLeft, SwipeRight, SwipeUp, SwipeDown, Spread, Pinch }

/// <summary>
/// Reads Precision Touchpad HID reports through Raw Input and recognises macOS‑style multi‑finger gestures:
/// three/four‑finger swipes in four directions and thumb+three‑finger spread/pinch.
/// </summary>
public sealed class TouchpadGestureService : IDisposable
{
    private const uint RIDEV_DEVNOTIFY = 0x00002000;
    private readonly WndProcDelegate _wndProc;
    private IntPtr _hwnd;
    private readonly Dictionary<IntPtr, DeviceCaps> _devices = new();
    private readonly Dictionary<uint, Contact> _contacts = new();

    // gesture state
    private bool _active;
    private int _fingers;
    private double _startX, _startY, _lastX, _lastY, _startSpread;
    private long _startTick;
    private bool _verticalFired, _spreadFired;
    private int _horizontalFires;
    private bool _lockHorizontal, _lockVertical;

    public event Action<GestureKind, int>? Gesture;
    public bool DeviceFound => _devices.Count > 0;

    private sealed class Contact { public double X, Y; public bool Tip; public long Seen; }

    private sealed class DeviceCaps
    {
        public IntPtr Preparsed;
        public Hid.HIDP_CAPS Caps;
        public ushort[] ContactCollections = Array.Empty<ushort>();
        public ushort ContactCountCollection;
        public bool HasContactCount;
        public double MaxX = 1, MaxY = 1;
    }

    public TouchpadGestureService()
    {
        _wndProc = WndProc;
        var wc = new User32.WNDCLASS { lpfnWndProc = _wndProc, lpszClassName = "WacOS.RawInputSink", hInstance = Kernel32.GetModuleHandle(null) };
        User32.RegisterClassW(ref wc);
        _hwnd = User32.CreateWindowEx(0, wc.lpszClassName, "WacOS raw input", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        var rid = new[] { new User32.RAWINPUTDEVICE { usUsagePage = Hid.UsagePage_Digitizer, usUsage = Hid.Usage_TouchPad, dwFlags = User32.RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = _hwnd } };
        if (!User32.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>()))
            Log.Warn("RegisterRawInputDevices(touchpad) failed: " + Marshal.GetLastWin32Error());
        else Log.Info("Touchpad raw input registered");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_INPUT)
        {
            try { OnRawInput(lParam); } catch (Exception ex) { Log.Debug("raw input: " + ex.Message); }
            return IntPtr.Zero;
        }
        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private unsafe void OnRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
        User32.GetRawInputData(hRawInput, User32.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        var buf = stackalloc byte[(int)size];
        var p = (IntPtr)buf;
        if (User32.GetRawInputData(hRawInput, User32.RID_INPUT, p, ref size, headerSize) != size) return;
        var header = Marshal.PtrToStructure<User32.RAWINPUTHEADER>(p);
        if (header.dwType != User32.RIM_TYPEHID) return;
        var hid = Marshal.PtrToStructure<User32.RAWHID>(p + (int)headerSize);
        var dev = GetDevice(header.hDevice);
        if (dev == null) return;
        IntPtr data = p + (int)headerSize + Marshal.SizeOf<User32.RAWHID>();
        for (uint i = 0; i < hid.dwCount; i++)
        {
            ParseReport(dev, data + (int)(i * hid.dwSizeHid), hid.dwSizeHid);
        }
    }

    private DeviceCaps? GetDevice(IntPtr hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var d)) return d;
        uint size = 0;
        User32.GetRawInputDeviceInfo(hDevice, User32.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) { return null; }
        var pre = Marshal.AllocHGlobal((int)size);
        if (User32.GetRawInputDeviceInfo(hDevice, User32.RIDI_PREPARSEDDATA, pre, ref size) == unchecked((uint)-1)) { Marshal.FreeHGlobal(pre); return null; }
        var dev = new DeviceCaps { Preparsed = pre };
        if (Hid.HidP_GetCaps(pre, out dev.Caps) != Hid.HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(pre); return null; }
        ushort n = dev.Caps.NumberInputValueCaps;
        var caps = new Hid.HIDP_VALUE_CAPS[Math.Max(n, (ushort)1)];
        if (n > 0 && Hid.HidP_GetValueCaps(Hid.HidP_Input, caps, ref n, pre) == Hid.HIDP_STATUS_SUCCESS)
        {
            var collections = new SortedSet<ushort>();
            for (int i = 0; i < n; i++)
            {
                var c = caps[i];
                ushort usage = c.UsageMin;
                if (c.UsagePage == Hid.UsagePage_Digitizer && usage == Hid.Usage_ContactCount) { dev.HasContactCount = true; dev.ContactCountCollection = c.LinkCollection; }
                if (c.UsagePage == Hid.UsagePage_Digitizer && usage == Hid.Usage_ContactId) collections.Add(c.LinkCollection);
                if (c.UsagePage == Hid.UsagePage_Generic && usage == Hid.Usage_X && c.LogicalMax > 0) dev.MaxX = c.LogicalMax;
                if (c.UsagePage == Hid.UsagePage_Generic && usage == Hid.Usage_Y && c.LogicalMax > 0) dev.MaxY = c.LogicalMax;
            }
            dev.ContactCollections = collections.ToArray();
        }
        _devices[hDevice] = dev;
        Log.Info($"Touchpad device: {dev.ContactCollections.Length} contact collections, range {dev.MaxX}x{dev.MaxY}");
        return dev;
    }

    private void ParseReport(DeviceCaps dev, IntPtr report, uint len)
    {
        long now = Environment.TickCount64;
        uint contactCount = 0;
        if (dev.HasContactCount)
            Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UsagePage_Digitizer, dev.ContactCountCollection, Hid.Usage_ContactCount, out contactCount, dev.Preparsed, report, len);

        var tipList = new ushort[8];
        foreach (var coll in dev.ContactCollections)
        {
            if (Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UsagePage_Digitizer, coll, Hid.Usage_ContactId, out uint id, dev.Preparsed, report, len) != Hid.HIDP_STATUS_SUCCESS) continue;
            Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UsagePage_Generic, coll, Hid.Usage_X, out uint x, dev.Preparsed, report, len);
            Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UsagePage_Generic, coll, Hid.Usage_Y, out uint y, dev.Preparsed, report, len);
            uint tl = (uint)tipList.Length;
            bool tip = false;
            if (Hid.HidP_GetUsages(Hid.HidP_Input, Hid.UsagePage_Digitizer, coll, tipList, ref tl, dev.Preparsed, report, len) == Hid.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < tl; i++) if (tipList[i] == Hid.Usage_TipSwitch) tip = true;
            // Devices report unused contact slots with x=y=0 and id 0; skip duplicates of an already-seen id in this report.
            if (!tip && !_contacts.ContainsKey(id)) continue;
            if (!_contacts.TryGetValue(id, out var c)) _contacts[id] = c = new Contact();
            c.X = x / dev.MaxX; c.Y = y / dev.MaxY; c.Tip = tip; c.Seen = now;
        }
        // Drop lifted contacts and stale ones.
        foreach (var k in _contacts.Where(kv => !kv.Value.Tip || now - kv.Value.Seen > 150).Select(kv => kv.Key).ToList()) _contacts.Remove(k);
        // In hybrid mode the frame arrives over several reports; process when we have at least the declared count (or every report if unknown).
        if (contactCount > 0 && _contacts.Count < contactCount) return;
        ProcessFrame();
    }

    private void ProcessFrame()
    {
        int n = _contacts.Count;
        if (n < 3)
        {
            if (_active) EndGesture();
            return;
        }
        double cx = _contacts.Values.Average(c => c.X), cy = _contacts.Values.Average(c => c.Y);
        double spread = _contacts.Values.Average(c => Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)));
        if (!_active)
        {
            _active = true; _fingers = n; _startX = _lastX = cx; _startY = _lastY = cy; _startSpread = spread; _startTick = Environment.TickCount64;
            _verticalFired = _spreadFired = false; _horizontalFires = 0; _lockHorizontal = _lockVertical = false;
            return;
        }
        // Finger count may settle during the first 80 ms of the gesture.
        if (n > _fingers && Environment.TickCount64 - _startTick < 80) { _fingers = n; _startX = cx; _startY = cy; _startSpread = spread; }

        double sens = Math.Clamp(App.Settings.Current.SwipeSensitivity, 0.4, 2.5);
        double dx = cx - _startX, dy = cy - _startY;
        double hThreshold = (_horizontalFires == 0 ? 0.13 : 0.28) / sens;
        double vThreshold = 0.12 / sens;

        if (!_lockVertical && !_verticalFired && Math.Abs(dx) > hThreshold && Math.Abs(dx) > Math.Abs(dy) * 1.3)
        {
            _lockHorizontal = true; _horizontalFires++;
            Fire(dx < 0 ? GestureKind.SwipeLeft : GestureKind.SwipeRight);
            _startX = cx; _startY = cy;
            return;
        }
        if (!_lockHorizontal && !_verticalFired && Math.Abs(dy) > vThreshold && Math.Abs(dy) > Math.Abs(dx) * 1.3)
        {
            _lockVertical = true; _verticalFired = true;
            Fire(dy < 0 ? GestureKind.SwipeUp : GestureKind.SwipeDown);
            return;
        }
        if (!_lockHorizontal && !_lockVertical && !_spreadFired && _fingers >= 4 && _startSpread > 0.01)
        {
            double ratio = spread / _startSpread;
            if (ratio > 1.35) { _spreadFired = true; Fire(GestureKind.Spread); }
            else if (ratio < 0.68) { _spreadFired = true; Fire(GestureKind.Pinch); }
        }
        _lastX = cx; _lastY = cy;
    }

    private void Fire(GestureKind kind)
    {
        Log.Debug($"Gesture {kind} fingers={_fingers}");
        try { Gesture?.Invoke(kind, _fingers); } catch (Exception ex) { Log.Error("Gesture handler", ex); }
    }

    private void EndGesture() { _active = false; _contacts.Clear(); }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) { User32.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        foreach (var d in _devices.Values) Marshal.FreeHGlobal(d.Preparsed);
        _devices.Clear();
    }
}
