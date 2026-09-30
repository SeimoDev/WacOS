using System.Runtime.InteropServices;

namespace WacOS.Native;

public static class Hid
{
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HidP_Input = 0;

    public const ushort UsagePage_Generic = 0x01, UsagePage_Digitizer = 0x0D;
    public const ushort Usage_TouchPad = 0x05, Usage_X = 0x30, Usage_Y = 0x31, Usage_TipSwitch = 0x42, Usage_ContactId = 0x51,
        Usage_ContactCount = 0x54, Usage_ScanTime = 0x56, Usage_Confidence = 0x47;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    // HIDP_VALUE_CAPS is a union-heavy struct of 72 bytes; we lay it out explicitly.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(17)] public byte Reserved;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(22)] public ushort Reserved2a;
        [FieldOffset(24)] public ushort Reserved2b;
        [FieldOffset(26)] public ushort Reserved2c;
        [FieldOffset(28)] public ushort Reserved2d;
        [FieldOffset(30)] public ushort Reserved2e;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        // Range / NotRange union
        [FieldOffset(56)] public ushort UsageMin;      // NotRange.Usage
        [FieldOffset(58)] public ushort UsageMax;
        [FieldOffset(60)] public ushort StringMin;
        [FieldOffset(62)] public ushort StringMax;
        [FieldOffset(64)] public ushort DesignatorMin;
        [FieldOffset(66)] public ushort DesignatorMax;
        [FieldOffset(68)] public ushort DataIndexMin;
        [FieldOffset(70)] public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_BUTTON_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public ushort ReportCount;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
        [FieldOffset(68)] public ushort DataIndexMin;
        [FieldOffset(70)] public ushort DataIndexMax;
    }

    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort length, IntPtr preparsedData);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort length, IntPtr preparsedData);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsedData, IntPtr report, uint reportLength);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData, IntPtr report, uint reportLength);
    [DllImport("hid.dll")] public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsedData);
}
