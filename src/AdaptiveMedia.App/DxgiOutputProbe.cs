using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace AdaptiveMedia;

internal sealed record DxgiOutputInfo(string AdapterName, uint AdapterLow, int AdapterHigh,
    uint VendorId, uint DeviceId, uint OutputIndex, string DeviceName, bool Attached,
    uint BitsPerColor, int ColorSpace, float[] Red, float[] Green, float[] Blue, float[] White,
    float MinLuminance, float MaxLuminance, float MaxFullFrameLuminance);

internal static unsafe class DxgiOutputProbe
{
    private static readonly Guid FactoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid Output6Iid = new("068346e8-aaec-4b84-add7-137f513f77a1");

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public Rect DesktopCoordinates;
        public int AttachedToDesktop, Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Red[2], Green[2], Blue[2], White[2];
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    private static nint Slot(nint ptr, int index) => (*(nint**)ptr)[index];

    public static IReadOnlyList<DxgiOutputInfo> Read()
    {
        var rows = new List<DxgiOutputInfo>();
        if (CreateDXGIFactory1(in FactoryIid, out nint factory) != 0) return rows;
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12);
            for (uint ai = 0; ai < 16; ai++)
            {
                nint adapter = 0;
                if (enumAdapters(factory, ai, &adapter) != 0) break;
                try
                {
                    AdapterDesc1 a = default;
                    var getAdapterDesc = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)Slot(adapter, 10);
                    if (getAdapterDesc(adapter, &a) != 0) continue;
                    string adapterName = new(a.Description);
                    var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7);
                    for (uint oi = 0; oi < 16; oi++)
                    {
                        nint output = 0;
                        if (enumOutputs(adapter, oi, &output) != 0) break;
                        try
                        {
                            Guid iid = Output6Iid;
                            if (Marshal.QueryInterface(output, in iid, out nint output6) != 0) continue;
                            try
                            {
                                OutputDesc1 d = default;
                                var getOutputDesc = (delegate* unmanaged[Stdcall]<nint, OutputDesc1*, int>)Slot(output6, 27);
                                if (getOutputDesc(output6, &d) != 0) continue;
                                string deviceName = new(d.DeviceName);
                                rows.Add(new DxgiOutputInfo(adapterName, a.AdapterLuid.Low, a.AdapterLuid.High,
                                    a.VendorId, a.DeviceId, oi, deviceName, d.AttachedToDesktop != 0,
                                    d.BitsPerColor, d.ColorSpace, [d.Red[0], d.Red[1]], [d.Green[0], d.Green[1]],
                                    [d.Blue[0], d.Blue[1]], [d.White[0], d.White[1]], d.MinLuminance,
                                    d.MaxLuminance, d.MaxFullFrameLuminance));
                            }
                            finally { Marshal.Release(output6); }
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return rows;
    }
}
