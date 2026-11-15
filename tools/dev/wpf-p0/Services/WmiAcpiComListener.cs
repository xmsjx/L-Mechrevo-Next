using System;
using System.Runtime.InteropServices;
using MechrevoLite;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 直接走 COM 的 ACPI 事件流：半同步通知查询 + 带超时的逐条取事件。
/// 与第三方控制台使用的取事件方式一致（只读，不改任何硬件）。
/// </summary>
internal sealed class WmiAcpiComListener : IDisposable
{
    private object? _locator;
    private object? _services;
    private object? _eventSource;

    internal bool TryStart(out string detail)
    {
        detail = "";
        try
        {
            Type? type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type == null)
            {
                detail = "WbemScripting 不可用";
                return false;
            }
            _locator = Activator.CreateInstance(type);
            if (_locator == null)
            {
                detail = "创建 SWbemLocator 失败";
                return false;
            }
            dynamic locator = _locator;
            _services = locator.ConnectServer(".", "root\\WMI");
            dynamic services = _services!;
            // 48 = WBEM_FLAG_FORWARD_ONLY(0x20) | WBEM_FLAG_RETURN_IMMEDIATELY(0x10)
            _eventSource = services.ExecNotificationQuery("SELECT * FROM AcpiTest_EventULong", "WQL", 48);
            detail = "COM · flags=0x30";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + " " + ex.Message;
            Dispose();
            return false;
        }
    }

    internal string LastError { get; private set; } = "";
    internal bool TryNext(int timeoutMs, out int code)
    {
        code = -1;
        try
        {
            dynamic source = _eventSource!;
            dynamic evt = source.NextEvent(timeoutMs);
            if (evt == null)
            {
                return false;
            }
            dynamic prop = evt.Properties_.Item("ULong");
            code = Convert.ToInt32(prop.Value);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        Release(_eventSource);
        Release(_services);
        Release(_locator);
        _eventSource = null;
        _services = null;
        _locator = null;
    }

    private static void Release(object? comObject)
    {
        if (comObject == null)
        {
            return;
        }
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
        }
    }
}
