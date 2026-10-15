using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WhSnap.Services;

/// <summary>
/// 剪贴板写入。同时写入 DIB 位图与 PNG 流：
/// PNG 格式能被 Office / 浏览器等更"挑格式"的程序识别，DIB 保证通用粘贴可用。
/// </summary>
internal static class ClipboardService
{
    // 保持 PNG 流存活：剪贴板延迟渲染期间流不能被回收
    private static MemoryStream? _pngStream;

    /// <summary>最近一次由 WhSnap 自己写入剪贴板的时间（剪贴板监听用它避免"自己写的又贴回来"）。</summary>
    public static DateTime LastWriteUtc { get; private set; } = DateTime.MinValue;

    public static bool TryCopy(BitmapSource image, out string error)
    {
        error = string.Empty;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var data = new DataObject();

                // 预乘 alpha 的位图（WPF 渲染结果）不能直接进 DIB/PNG 通道，否则半透明处会发暗
                var normalized = Services.SaveService.Normalize(image);
                data.SetImage(normalized);

                var stream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(normalized));
                encoder.Save(stream);
                stream.Position = 0;

                _pngStream = stream;
                data.SetData("PNG", stream, true);

                Clipboard.SetDataObject(data, true);
                LastWriteUtc = DateTime.UtcNow;
                return true;
            }
            catch (COMException ex)
            {
                error = ex.Message;
                Thread.Sleep(80 * attempt);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                break;
            }
        }

        if (string.IsNullOrEmpty(error))
        {
            error = "剪贴板被其他程序占用。";
        }

        return false;
    }
}
