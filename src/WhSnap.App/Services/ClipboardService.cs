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

    public static bool TryCopy(BitmapSource image, out string error)
    {
        error = string.Empty;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(image);

                var stream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
                stream.Position = 0;

                _pngStream = stream;
                data.SetData("PNG", stream, true);

                Clipboard.SetDataObject(data, true);
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
