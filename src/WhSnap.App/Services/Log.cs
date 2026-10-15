using System.IO;
using System.Text;

namespace WhSnap.Services;

/// <summary>极简文件日志。预览版不引入日志库，保持零 NuGet 依赖。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;

    public static string LogDirectory => Path.Combine(SettingsService.ConfigDirectory, "logs");

    public static string CurrentLogFile
    {
        get
        {
            if (_filePath is null)
            {
                _filePath = Path.Combine(LogDirectory, $"whsnap-{DateTime.Now:yyyyMMdd}.log");
            }

            return _filePath;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERR ", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
