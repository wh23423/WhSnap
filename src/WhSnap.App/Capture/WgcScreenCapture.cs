using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WhSnap.Core;

namespace WhSnap.Capture;

/// <summary>
/// Windows.Graphics.Capture 采集后端（每台显示器创建一个采集项，合成整张虚拟桌面）。
///
/// 定位：比 DXGI 更能覆盖部分硬件叠加层内容，是 Win11 上的首选路径。
///
/// 实现要点（都是踩过的坑）：
///   * CsWinRT 未投影 IGraphicsCaptureItemInterop，这里通过 RoGetActivationFactory 直接取该接口；
///     传给 CreateForMonitor 的 IID 必须用 WinRT.GuidGenerator.GetIID()——
///     typeof(GraphicsCaptureItem).GUID 是 CsWinRT 生成的内部标识，不是 COM 接口 IID。
///   * WinRT 的 IDirect3DDevice 用 d3d11.dll 原生导出 + WinRT.MarshalInspectable.FromAbi 获取；
///     第三方封装的泛型转换实测会抛 InvalidCastException。
///   * CreateFreeThreaded 不要求 DispatcherQueue，适合在任意线程做单帧冻结抓取。
///   * 采集期间系统可能在屏幕边缘短暂显示采集边框，单帧抓取后立即释放，影响很小。
/// </summary>
internal sealed class WgcScreenCapture : IScreenCapture
{
    private const int FrameWaitMs = 1500;
    private const int FramePollMs = 15;

    /// <summary>IGraphicsCaptureItemInterop 的 IID（Windows SDK 固定值）。</summary>
    private static readonly Guid ItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    public string Name => CaptureBackendNames.Wgc;

    public CaptureFrame Capture()
    {
        var stopwatch = Stopwatch.StartNew();
        var monitors = MonitorEnumerator.Enumerate();
        var bounds = VirtualDesktop.BoundsOf(monitors);
        var canvas = new byte[bounds.Width * 4 * bounds.Height];
        var notes = new List<string>();

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new CaptureNotSupportedException("当前系统不支持 Windows.Graphics.Capture。");
        }

        using ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!)
            ?? throw new InvalidOperationException("D3D11CreateDevice 返回 null。");

        using ID3D11DeviceContext context = device.ImmediateContext
            ?? throw new InvalidOperationException("无法获取 D3D11 立即上下文。");

        IDirect3DDevice winrtDevice = CreateWinRtDevice(device);

        int captured = 0;
        foreach (var monitor in monitors)
        {
            if (CaptureMonitor(monitor, winrtDevice, device, context, bounds, canvas, notes))
            {
                captured++;
            }
        }

        if (captured == 0)
        {
            string detail = notes.Count == 0 ? string.Empty : " 原因：" + string.Join("；", notes);

            if (notes.All(n => n.Contains("像素格式", StringComparison.Ordinal)))
            {
                throw new CaptureNotSupportedException("WGC 后端不支持当前显示输出的像素格式（疑似 HDR/宽色域）。" + detail);
            }

            throw new InvalidOperationException("WGC 未捕获到任何显示器画面。" + detail);
        }

        var image = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgra32, null, canvas, bounds.Width * 4);
        image.Freeze();

        if (!FrameSanity.LooksLikeRealContent(image, out string sanity))
        {
            throw new CaptureNotSupportedException($"WGC 抓到的画面完全均匀（{sanity}），当前环境可能无法通过 WGC 获取桌面画面。");
        }

        return new CaptureFrame(
            image,
            bounds,
            monitors,
            Name,
            stopwatch.Elapsed,
            ProtectedContentMasked: false,
            Notes: notes.Count == 0 ? null : string.Join("；", notes));
    }

    /// <summary>
    /// 由 ID3D11Device 得到 WinRT 的 IDirect3DDevice。
    /// 直接调用 d3d11.dll 的原生导出并用 WinRT.MarshalInspectable 包装，
    /// 不依赖第三方封装的泛型转换（后者在本机实测抛 InvalidCastException）。
    /// </summary>
    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();

        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
        if (hr < 0 || inspectable == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败（0x{hr:X8}）。");
        }

        // FromAbi 接管该引用，因此不再 Marshal.Release
        return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
    }

    private static bool CaptureMonitor(
        MonitorInfo monitor,
        IDirect3DDevice winrtDevice,
        ID3D11Device device,
        ID3D11DeviceContext context,
        PhysicalRect bounds,
        byte[] canvas,
        List<string> notes)
    {
        // 分步记录，失败时能直接指出是哪一步——采集链路的失败原因区分度很低，不这么做只能靠猜
        string step = "创建采集项";

        try
        {
            GraphicsCaptureItem item = CreateItemForMonitor(monitor.Handle);
            var size = item.Size;
            var itemBounds = new PhysicalRect(monitor.Bounds.X, monitor.Bounds.Y, size.Width, size.Height);

            if (size.Width != monitor.Bounds.Width || size.Height != monitor.Bounds.Height)
            {
                notes.Add($"显示器 {monitor.Bounds} 的采集项尺寸为 {size.Width}x{size.Height}，与显示器矩形不一致");
            }

            step = "创建帧池";
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            step = "创建采集会话";
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            step = "启动采集";
            session.StartCapture();

            step = "等待首帧";
            using Direct3D11CaptureFrame? frame = WaitForFrame(pool);
            if (frame is null)
            {
                notes.Add($"显示器 {monitor.Bounds} 在 {FrameWaitMs} ms 内未收到帧");
                return false;
            }

            step = "取 D3D 纹理";
            using ID3D11Texture2D texture = GetTextureFromSurface(frame.Surface);
            var description = texture.Description;
            uint format = (uint)description.Format;

            if (!D3D11TextureReader.IsBgra8(format))
            {
                notes.Add($"显示器 {monitor.Bounds} 像素格式为 DXGI_FORMAT {format}，WGC 后端未支持");
                return false;
            }

            step = "读取像素";
            byte[] pixels = D3D11TextureReader.ReadBgra(device, context, texture, description);

            step = "合成到虚拟桌面";
            string? composeNote = D3D11TextureReader.Compose(
                pixels,
                (int)description.Width,
                (int)description.Height,
                itemBounds,
                DisplayRotation.Identity, // WGC 返回的是显示器当前方向的合成结果，无需再旋转
                bounds,
                canvas);

            if (!string.IsNullOrWhiteSpace(composeNote))
            {
                notes.Add($"显示器 {monitor.Bounds}：{composeNote}");
            }

            return true;
        }
        catch (Exception ex)
        {
            notes.Add($"显示器 {monitor.Bounds} 在「{step}」失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 由 WinRT 的 IDirect3DSurface 取出 ID3D11Texture2D。
    ///
    /// 不能用第三方封装（实测 Vortice 的 GetDXGISurface 会把 CsWinRT 投影对象强转成它自己的
    /// ComImport 接口并抛 InvalidCastException）。正确路径是 WinRT 标准做法：
    ///   1) 通过 WinRT.IWinRTObject.NativeObject 拿到 ABI 指针并 QI 出 IDirect3DDxgiInterfaceAccess
    ///   2) 调它的 GetInterface(IID_ID3D11Texture2D)（虚表槽 3）
    /// </summary>
    private static unsafe ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        if (surface is not WinRT.IWinRTObject winrtObject)
        {
            throw new InvalidOperationException("WinRT 表面对象未实现 IWinRTObject，无法取得 ABI 指针。");
        }

        using WinRT.IObjectReference accessReference = winrtObject.NativeObject.As(DxgiInterfaceAccessIid);
        IntPtr access = accessReference.ThisPtr;
        if (access == IntPtr.Zero)
        {
            throw new InvalidOperationException("QI IDirect3DDxgiInterfaceAccess 失败。");
        }

        // IDirect3DDxgiInterfaceAccess：IUnknown 占 0..2，GetInterface = 3
        IntPtr vtable = Marshal.ReadIntPtr(access);
        IntPtr entry = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);

        var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)entry;
        Guid textureIid = Texture2DIid;
        IntPtr texturePointer;
        int hr = getInterface(access, &textureIid, &texturePointer);

        if (hr < 0)
        {
            throw new InvalidOperationException($"IDirect3DDxgiInterfaceAccess::GetInterface 失败（HRESULT 0x{hr:X8}）。");
        }

        if (texturePointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetInterface 返回空纹理指针。");
        }

        // 指针携带一个引用，交给 Vortice 包装对象接管并在 Dispose 时释放
        return new ID3D11Texture2D(texturePointer);
    }

    /// <summary>IDirect3DDxgiInterfaceAccess 的 IID（Windows SDK 固定值）。</summary>
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>ID3D11Texture2D 的 IID（D3D11 固定值）。</summary>
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static Direct3D11CaptureFrame? WaitForFrame(Direct3D11CaptureFramePool pool)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.ElapsedMilliseconds < FrameWaitMs)
        {
            Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                return frame;
            }

            Thread.Sleep(FramePollMs);
        }

        return null;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        if (monitor == 0)
        {
            throw new InvalidOperationException("显示器句柄为空，无法创建采集项。");
        }

        Guid itemIid = ResolveCaptureItemIid();
        IntPtr factory = GetItemInteropPointer();
        try
        {
            return CreateForMonitor(factory, monitor, itemIid);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>
    /// 解析 IGraphicsCaptureItem 的 IID。
    /// 必须用投影程序集里该接口的 [Guid]：WinRT.GuidGenerator.GetIID(typeof(GraphicsCaptureItem))
    /// 与 typeof(GraphicsCaptureItem).GUID 返回的都是 CsWinRT 为该"运行时类"计算的标识
    /// （实测 642fe673-c974-339f-98dc-c7b314308097），拿它去 CreateForMonitor 会得到
    /// E_NOINTERFACE(0x80004002)。
    /// </summary>
    private static Guid ResolveCaptureItemIid()
    {
        var assembly = typeof(GraphicsCaptureItem).Assembly;

        var interfaceType = assembly.GetType("Windows.Graphics.Capture.IGraphicsCaptureItem")
                            ?? assembly.GetTypes().FirstOrDefault(t => t.Name == "IGraphicsCaptureItem");

        if (interfaceType is not null)
        {
            if (interfaceType.GUID != Guid.Empty)
            {
                return interfaceType.GUID;
            }

            var attribute = interfaceType.GetCustomAttribute<GuidAttribute>();
            if (attribute is not null && Guid.TryParse(attribute.Value, out Guid parsed))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException("无法从投影程序集解析 IGraphicsCaptureItem 的 IID。");
    }

    /// <summary>
    /// 直接调用 IGraphicsCaptureItemInterop 的虚表槽。
    /// 不走 COM 封送（[ComImport] + Marshal.GetObjectForIUnknown 这条路的失败信息几乎无法区分），
    /// 这样能拿到真实的 HRESULT，出错时能直接看出是"接口取不到"还是"参数不对"。
    /// </summary>
    private static unsafe GraphicsCaptureItem CreateForMonitor(IntPtr factory, nint monitor, Guid itemIid)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        if (vtable == IntPtr.Zero)
        {
            throw new InvalidOperationException("激活工厂虚表为空。");
        }

        // IUnknown 占 0..2；IGraphicsCaptureItemInterop：CreateForWindow = 3，CreateForMonitor = 4
        IntPtr entry = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        if (entry == IntPtr.Zero)
        {
            throw new InvalidOperationException("IGraphicsCaptureItemInterop 未提供 CreateForMonitor。");
        }

        var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)entry;
        IntPtr itemPointer;
        int hr = createForMonitor(factory, monitor, &itemIid, &itemPointer);

        if (hr < 0)
        {
            throw new InvalidOperationException($"CreateForMonitor 失败（HRESULT 0x{hr:X8}，IID {itemIid}）。");
        }

        if (itemPointer == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateForMonitor 返回空指针（IID {itemIid}）。");
        }

        return GraphicsCaptureItem.FromAbi(itemPointer);
    }

    /// <summary>取得 IGraphicsCaptureItemInterop 的原始指针，调用方负责 Marshal.Release。</summary>
    private static IntPtr GetItemInteropPointer()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        int hr = WindowsCreateString(className, className.Length, out IntPtr hstring);
        if (hr < 0)
        {
            throw new InvalidOperationException($"WindowsCreateString 失败（0x{hr:X8}）。");
        }

        try
        {
            Guid iid = ItemInteropIid;
            hr = RoGetActivationFactory(hstring, ref iid, out IntPtr factory);
            if (hr < 0)
            {
                throw new InvalidOperationException($"RoGetActivationFactory 失败（0x{hr:X8}）。");
            }

            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
