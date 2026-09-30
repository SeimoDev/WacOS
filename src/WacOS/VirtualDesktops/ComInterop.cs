// Undocumented Windows 11 virtual desktop COM interfaces (24H2 / 25H2, builds 26100+).
// Derived from public research (MScholtes/VirtualDesktop, Grabacr07/VirtualDesktop, Ciantic/VirtualDesktopAccessor).
using System.Runtime.InteropServices;

namespace WacOS.VirtualDesktops;

internal static class VdGuids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    public static readonly Guid CLSID_VirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    public static readonly Guid IID_IVirtualDesktop = new("3F07F4BE-B107-441A-AF0F-39D82529072C");
    public static readonly Guid IID_IApplicationView = new("372E1D3B-38D3-42E4-A15B-8AB2B178F513");
}

[StructLayout(LayoutKind.Sequential)] internal struct VdSize { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct VdRect { public int Left, Top, Right, Bottom; }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView
{
    // IInspectable slots (the .NET runtime cannot marshal IInspectable directly, so they are declared explicitly).
    int GetIids(out int iidCount, out IntPtr iids);
    int GetRuntimeClassName(out IntPtr className);
    int GetTrustLevel(out int trustLevel);
    int SetFocus();
    int SwitchTo();
    int TryInvokeBack(IntPtr callback);
    int GetThumbnailWindow(out IntPtr hwnd);
    int GetMonitor(out IntPtr immersiveMonitor);
    int GetVisibility(out int visibility);
    int SetCloak(int cloakType, int unknown);
    int GetPosition(ref Guid guid, out IntPtr position);
    int SetPosition(ref IntPtr position);
    int InsertAfterWindow(IntPtr hwnd);
    int GetExtendedFramePosition(out VdRect rect);
    int GetAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int SetAppUserModelId(string id);
    int IsEqualByAppUserModelId(string id, out int result);
    int GetViewState(out uint state);
    int SetViewState(uint state);
    int GetNeediness(out int neediness);
    int GetLastActivationTimestamp(out ulong timestamp);
    int SetLastActivationTimestamp(ulong timestamp);
    int GetVirtualDesktopId(out Guid guid);
    int SetVirtualDesktopId(ref Guid guid);
    int GetShowInSwitchers(out int flag);
    int SetShowInSwitchers(int flag);
    int GetScaleFactor(out int factor);
    int CanReceiveInput(out bool canReceiveInput);
    int GetCompatibilityPolicyType(out int flags);
    int SetCompatibilityPolicyType(int flags);
    int GetSizeConstraints(IntPtr monitor, out VdSize size1, out VdSize size2);
    int GetSizeConstraintsForDpi(uint uint1, out VdSize size1, out VdSize size2);
    int SetSizeConstraintsForDpi(ref uint uint1, ref VdSize size1, ref VdSize size2);
    int OnMinSizePreferencesUpdated(IntPtr hwnd);
    int ApplyOperation(IntPtr operation);
    int IsTray(out bool isTray);
    int IsInHighZOrderBand(out bool isInHighZOrderBand);
    int IsSplashScreenPresented(out bool isSplashScreenPresented);
    int Flash();
    int GetRootSwitchableOwner(out IApplicationView rootSwitchableOwner);
    int EnumerateOwnershipTree(out IObjectArray ownershipTree);
    int GetEnterpriseId([MarshalAs(UnmanagedType.LPWStr)] out string enterpriseId);
    int IsMirrored(out bool isMirrored);
    int Unknown1(out int unknown);
    int Unknown2(out int unknown);
    int Unknown3(out int unknown);
    int Unknown4(out int unknown);
    int Unknown5(out int unknown);
    int Unknown6(int unknown);
    int Unknown7();
    int Unknown8(out int unknown);
    int Unknown9(int unknown);
    int Unknown10(int unknownX, int unknownY);
    int Unknown11(int unknown);
    int Unknown12(out VdSize size1);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    int GetViews(out IObjectArray array);
    int GetViewsByZOrder(out IObjectArray array);
    int GetViewsByAppUserModelId(string id, out IObjectArray array);
    int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    int GetViewForApplication(object application, out IApplicationView view);
    int GetViewForAppUserModelId(string id, out IApplicationView view);
    int GetViewInFocus(out IntPtr view);
    int Unknown1(out IntPtr view);
    void RefreshCollection();
    int RegisterForApplicationViewChanges(object listener, out int cookie);
    int UnregisterForApplicationViewChanges(int cookie);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IApplicationView view);
    Guid GetId();
    IntPtr GetName();            // HSTRING (the runtime cannot marshal HSTRING; see HString helper)
    IntPtr GetWallpaperPath();   // HSTRING
    bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(IApplicationView view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int nIndex);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopid);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray unknown1, out IObjectArray unknown2);
    void SetDesktopName(IVirtualDesktop desktop, IntPtr /* HSTRING */ name);
    void SetDesktopWallpaper(IVirtualDesktop desktop, IntPtr /* HSTRING */ path);
    void UpdateWallpaperPathForAllDesktops(IntPtr /* HSTRING */ path);
    void CopyDesktopState(IApplicationView pView0, IApplicationView pView1);
    void CreateRemoteDesktop(IntPtr /* HSTRING */ path, out IVirtualDesktop desktop);
    void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchtype);
    void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    void GetLastActiveDesktop(out IVirtualDesktop desktop);
    void WaitForAnimationToComplete();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
    Guid GetWindowDesktopId(IntPtr topLevelWindow);
    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
internal interface IVirtualDesktopPinnedApps
{
    bool IsAppIdPinned(string appId);
    void PinAppID(string appId);
    void UnpinAppID(string appId);
    bool IsViewPinned(IApplicationView applicationView);
    void PinView(IApplicationView applicationView);
    void UnpinView(IApplicationView applicationView);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);
    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
}

/// <summary>Manual HSTRING handling (WinRT strings) for the shell interfaces above.</summary>
internal static class HString
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    public static IntPtr Create(string s) { WindowsCreateString(s, s.Length, out var h); return h; }
    public static void Free(IntPtr h) { if (h != IntPtr.Zero) WindowsDeleteString(h); }

    /// <summary>Reads and releases an HSTRING returned by a COM call.</summary>
    public static string Take(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        try { var p = WindowsGetStringRawBuffer(h, out uint len); return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, (int)len) ?? ""; }
        finally { WindowsDeleteString(h); }
    }
}
