using System.Runtime.InteropServices;

namespace WhSnap.Interop;

/// <summary>
/// 崩溃转储所需的 dbghelp 声明（M6 第 6 项）。
///
/// 用自己调用 <c>MiniDumpWriteDump</c> 的方式抓 dump，**不注册 WER LocalDumps**——
/// 后者要写 HKLM 需要管理员权限，与"per-user 安装、免 UAC"的定位冲突。
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>只含调用栈与线程信息：体积可控（几 MB），够定位崩溃点。</summary>
    public const uint MiniDumpNormal = 0x00000000;

    public const uint MiniDumpWithThreadInfo = 0x00001000;

    /// <summary>
    /// 把指定进程的内存状态写成 dump 文件。
    /// hFile 必须是**可写**的文件句柄（用 FileStream.SafeFileHandle 传入）。
    /// </summary>
    [DllImport("dbghelp.dll", SetLastError = true)]
    public static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    /// <summary>dbghelp.dll 能否加载（判断"这台机器上 dump 是否可用"而不真写文件）。</summary>
    public static bool IsDumpApiAvailable()
    {
        if (!NativeLibrary.TryLoad("dbghelp.dll", out IntPtr handle))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(handle, "MiniDumpWriteDump", out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
