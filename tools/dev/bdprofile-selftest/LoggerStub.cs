// 自测桩：被测逻辑只调用两处日志方法，这里保持签名一致即可（不写文件）。
internal static class Logger
{
    internal static void WriteLineIfChanged(string key, string logMessage)
    {
    }

    internal static void WriteLine(string message)
    {
    }

    internal static void WriteError(string message)
    {
    }
}