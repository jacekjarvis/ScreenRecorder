using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenRecorder.Core.Interop;

/// <summary>Bridges Win32 handles and D3D11 objects to the WinRT capture API.</summary>
internal static unsafe class WinRtInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, slot: 3);
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, slot: 4);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out IntPtr hstring));
        try
        {
            Guid factoryIid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref factoryIid, out IntPtr factory));
            try
            {
                // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor
                var vtbl = *(IntPtr**)factory;
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];
                Guid itemIid = IID_IGraphicsCaptureItem;
                IntPtr item;
                Marshal.ThrowExceptionForHR(create(factory, handle, &itemIid, &item));
                try { return GraphicsCaptureItem.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in accessIid, out IntPtr access));
            try
            {
                // IDirect3DDxgiInterfaceAccess: [3] GetInterface
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid texIid = typeof(ID3D11Texture2D).GUID;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(getInterface(access, &texIid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(surfacePtr); }
    }
}
