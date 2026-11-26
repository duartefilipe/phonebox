#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PhoneBox.Agent;

/// <summary>Reads a captured picture while it is mapped: BGRA, rows <c>stride</c> bytes apart.</summary>
internal unsafe delegate void FrameReader(byte* bgra, int stride, int width, int height);

/// <summary>
/// One window's picture as the compositor draws it (Windows.Graphics.Capture), the way screen recorders and meeting
/// apps share a window: on the graphics card, at the window's own frame rate, whether or not it is in front, and
/// without asking the app to paint itself again as <c>PrintWindow</c> did (which made the browser stutter).
/// </summary>
/// <remarks>
/// A new frame arrives only when the window changes; <see cref="TryRead"/> hands over the newest one and drops older
/// ones, so the caller sets the pace. Content a player protects (DRM, hardware decoding) comes out black, as it does in
/// any screen capture: that is Windows' choice, not something to work around.
/// </remarks>
internal sealed unsafe class WindowCapture : IDisposable
{
    public nint Window { get; }
    public bool Closed => _closed;

    private nint _device, _context, _staging;
    private int _stagingWidth, _stagingHeight;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _held;
    private volatile bool _closed;
    private bool _disposed;

    /// <summary>Windows 10 1903 and later.</summary>
    public static bool Supported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    private WindowCapture(nint window)
    {
        Window = window;
        nint device = 0, context = 0, dxgi = 0, inspectable = 0;
        try
        {
            uint level;
            // BGRA support: the capture's pictures are BGRA
            Com.Check(D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0x20, null, 0, 7 /* SDK_VERSION */, &device, &level, &context), "D3D11CreateDevice");
            dxgi = Com.Query(device, IID_IDXGIDevice);
            Com.Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            _device = device;
            _context = context;
            device = context = 0;
        }
        finally
        {
            Com.Release(ref inspectable);
            Com.Release(ref dxgi);
            Com.Release(ref context);
            Com.Release(ref device);
        }

        _item = ItemFor(window);
        _item.Closed += (_, _) => _closed = true;
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        NoBorder(_session);
        _session.StartCapture();
    }

    /// <summary>Starts capturing <paramref name="window"/>; throws when it cannot be captured.</summary>
    public static WindowCapture Start(nint window) => new(window);

    /// <summary>
    /// Hands the newest picture since the last call to <paramref name="read"/>; false when the window has not changed
    /// (or is minimized, which stops the pictures).
    /// </summary>
    public bool TryRead(FrameReader read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Poll();
        var frame = _held;
        _held = null;
        if (frame is null) return false;
        using (frame)
        {
            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // the window was resized: the pool follows, and this picture (of the old size) is still good to use
                _poolSize = size;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
            }
            nint texture = 0;
            var access = Com.Query(((IWinRTObject)frame.Surface).NativeObject.ThisPtr, IID_IDirect3DDxgiInterfaceAccess);
            try
            {
                var iid = IID_ID3D11Texture2D;
                Com.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Vt(access)[3])(access, &iid, &texture), "GetInterface");
                TextureDesc desc;
                ((delegate* unmanaged<nint, TextureDesc*, void>)Com.Vt(texture)[10])(texture, &desc);
                var w = (int)Math.Min((uint)size.Width, desc.Width);
                var h = (int)Math.Min((uint)size.Height, desc.Height);
                if (w < 16 || h < 16) return false;
                EnsureStaging(w, h);
                var box = new Box { Right = (uint)w, Bottom = (uint)h, Back = 1 };
                ((delegate* unmanaged<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Com.Vt(_context)[46])
                    (_context, _staging, 0, 0, 0, 0, texture, 0, &box); // CopySubresourceRegion
                Mapped mapped;
                Com.Check(((delegate* unmanaged<nint, nint, uint, uint, uint, Mapped*, int>)Com.Vt(_context)[14])
                    (_context, _staging, 0, 1 /* READ */, 0, &mapped), "Map");
                try { read((byte*)mapped.Data, (int)mapped.RowPitch, w, h); }
                finally { ((delegate* unmanaged<nint, nint, uint, void>)Com.Vt(_context)[15])(_context, _staging, 0); } // Unmap
                return true;
            }
            finally
            {
                Com.Release(ref texture);
                Marshal.Release(access);
            }
        }
    }

    /// <summary>
    /// Keeps only the newest picture: the pool has a few buffers, and one left full of an old picture would leave the
    /// next ones nowhere to go, so what <see cref="TryRead"/> got would be stale.
    /// </summary>
    public void Poll()
    {
        if (_disposed) return;
        while (_pool.TryGetNextFrame() is { } next)
        {
            _held?.Dispose();
            _held = next;
        }
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging != 0 && _stagingWidth == width && _stagingHeight == height) return;
        Com.Release(ref _staging);
        var desc = new TextureDesc
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            SampleCount = 1, Usage = 3 /* STAGING */, CpuAccess = 0x20000, // READ
        };
        nint staging;
        Com.Check(((delegate* unmanaged<nint, TextureDesc*, nint, nint*, int>)Com.Vt(_device)[5])(_device, &desc, 0, &staging), "CreateTexture2D");
        _staging = staging;
        _stagingWidth = width;
        _stagingHeight = height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _held?.Dispose(); } catch { }
        _held = null;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { }
        Com.Release(ref _staging);
        Com.Release(ref _context);
        Com.Release(ref _device);
    }

    private static GraphicsCaptureItem ItemFor(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring = 0, factory = 0, item = 0;
        try
        {
            Com.Check(WindowsCreateString(name, name.Length, &hstring), "WindowsCreateString");
            var iid = IID_IGraphicsCaptureItemInterop;
            Com.Check(RoGetActivationFactory(hstring, &iid, &factory), "RoGetActivationFactory");
            var itemIid = IID_IGraphicsCaptureItem;
            Com.Check(((delegate* unmanaged<nint, nint, Guid*, nint*, int>)Com.Vt(factory)[3])(factory, window, &itemIid, &item), "CreateForWindow");
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Com.Release(ref item);
            Com.Release(ref factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// Windows 11 can capture without the yellow frame around the window; the 19041 projection this builds against
    /// does not know the property yet, so it is set through the interface that has it, when there is one.
    /// </summary>
    private static void NoBorder(GraphicsCaptureSession session)
    {
        try
        {
            var three = Com.TryQuery(((IWinRTObject)session).NativeObject.ThisPtr, IID_IGraphicsCaptureSession3);
            if (three == 0) return;
            try { ((delegate* unmanaged<nint, byte, int>)Com.Vt(three)[7])(three, 0); } // put_IsBorderRequired(false)
            finally { Marshal.Release(three); }
        }
        catch { }
    }

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* levels, uint levelCount,
        uint sdkVersion, nint* device, uint* level, nint* context);

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, nint* graphicsDevice);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
}

/// <summary>
/// Where things are in a captured window's picture, in its pixels: the client area, and the page of a Chromium browser
/// (its content window). The capture's picture is the window's visible frame (DWM's extended frame bounds).
/// </summary>
internal static class WindowGeometry
{
    internal readonly record struct Layout(PixelRect Client, (int X, int Y) ClientOnScreen, PixelRect? Viewport, string Title);

    /// <summary>Reads the window's layout with per-monitor DPI, so the numbers are the screen's pixels like the capture's.</summary>
    public static Layout? Of(nint window)
    {
        var old = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        try
        {
            if (DwmGetWindowAttribute(window, 9 /* EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<Rect>()) != 0
                && !GetWindowRect(window, out frame)) return null;
            if (!GetClientRect(window, out var client)) return null;
            var origin = new Point();
            if (!ClientToScreen(window, ref origin)) return null;
            var clientRect = new PixelRect(origin.X - frame.Left, origin.Y - frame.Top, client.Right - client.Left, client.Bottom - client.Top);
            PixelRect? viewport = null;
            long best = 0;
            var cls = new StringBuilder(64);
            EnumChildWindows(window, (child, _) =>
            {
                cls.Clear();
                if (GetClassName(child, cls, cls.Capacity) > 0 && cls.ToString() == "Chrome_RenderWidgetHostHWND"
                    && IsWindowVisible(child) && GetWindowRect(child, out var r))
                {
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > best) { best = area; viewport = new PixelRect(r.Left - frame.Left, r.Top - frame.Top, r.Right - r.Left, r.Bottom - r.Top); }
                }
                return true;
            }, 0);
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            return new Layout(clientRect, (origin.X, origin.Y), viewport, title.ToString());
        }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
    }

    private delegate bool EnumProc(nint window, nint param);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
#endif
