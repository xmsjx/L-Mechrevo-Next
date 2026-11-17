using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MechrevoLite.Hardware
{
    /// <summary>
    /// 宝龙达厂商驱动的 EC RAM 访问通道（只做"能读/能写"这一层，不含任何策略）。
    /// <para>驱动由厂商提供：先按 SCM 方式创建/启动内核服务，再通过厂商 DLL 做端口读写；
    /// EC RAM 走 0x2E/0x2F 间接窗口（16 位索引），不使用 ACPI EC 的 0x66/0x62，
    /// 因此不会与系统的 acpiec.sys 争抢端口。</para>
    /// <para>⚠️ 驱动文件必须存在（DLL + SYS）；缺文件/被内存完整性拦截/服务被禁用都会如实返回失败。</para>
    /// </summary>
    internal static class BaolongdaVendorEc
    {
        private const string DllName = "JiaoLongDriver64.dll";
        private const string LegacyDllName = "Bldring64.dll";
        private const string ServiceName = "JiaoLongDriver64";
        private const string LegacyServiceName = "Bldring64";

        private const int ScManagerAllAccess = 0xF003F;
        private const int ServiceAllAccess = 0xF01FF;
        private const uint ServiceKernelDriver = 0x00000001;
        private const uint ServiceDemandStart = 0x00000003;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceNoChange = 0xFFFFFFFF;
        private const int ServiceControlStop = 0x00000001;

        private const int EcIndexHighPort = 0x2E;
        private const int EcIndexLowPort = 0x2F;
        private const int EcWindowSelectHigh = 0x11;
        private const int EcWindowSelectLow = 0x10;
        private const int EcWindowAccess = 0x12;

        private static readonly object Gate = new object();

        /// <summary>允许访问的 EC RAM 地址（只在风扇接管需要的这几个上开口，避免误写别处）。</summary>
        private static readonly int[] AllowedAddresses = { 0xB20, 0xC83C, 0xC83D };

        private static bool IsAllowed(int address) => Array.IndexOf(AllowedAddresses, address) >= 0;
        private static IntPtr _dll;
        private static string _loadedService = string.Empty;
        private static bool _serviceCreated;
        private static bool _ready;
        private static bool _unavailable;
        private static bool _logged;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenService(IntPtr scm, string serviceName, int access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateService(IntPtr scm, string serviceName, string displayName, int access,
            uint serviceType, uint startType, uint errorControl, string binaryPath,
            string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName2);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr service, int argCount, IntPtr argPointers);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
            string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password, string? displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, int control, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool InitializeBldringDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ShutdownBldringDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool GetPortValDelegate(ushort port, ref byte value, byte size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool SetPortValDelegate(ushort port, byte value, byte size);

        private static InitializeBldringDelegate? _initialize;
        private static ShutdownBldringDelegate? _shutdown;
        private static GetPortValDelegate? _getPort;
        private static SetPortValDelegate? _setPort;

        /// <summary>通道是否已就绪（首次调用会尝试加载驱动）。</summary>
        internal static bool IsReady => _ready;

        /// <summary>尝试加载厂商驱动并初始化。失败会被记住，不再重复尝试。</summary>
        internal static bool EnsureReady()
        {
            lock (Gate)
            {
                if (_ready)
                {
                    return true;
                }
                if (_unavailable)
                {
                    return false;
                }
                try
                {
                    if (!TryLoadDriver(out string detail))
                    {
                        _unavailable = true;
                        if (!_logged)
                        {
                            _logged = true;
                            Logger.WriteLine("[fan] 厂商 EC 通道不可用：" + detail);
                        }
                        return false;
                    }
                    _ready = true;
                    Logger.WriteLine("[fan] 厂商 EC 通道已就绪（服务=" + _loadedService + "）");
                    return true;
                }
                catch (Exception ex)
                {
                    _unavailable = true;
                    Logger.WriteLine("[fan] 厂商 EC 通道初始化异常：" + ex.GetType().Name + " " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>读一个 EC RAM 字节（16 位索引，走 0x2E/0x2F 窗口）。</summary>
        internal static bool TryReadByte(int address, out byte value)
        {
            value = 0;
            if (!IsAllowed(address) || !EnsureReady())
            {
                return false;
            }
            lock (Gate)
            {
                if (!SelectWindow(address))
                {
                    return false;
                }
                // 0x2E 是命令/索引口，0x2F 是数据口：先向 0x2E 选"数据寄存器"，再从 0x2F 读。
                if (!WritePort(EcIndexHighPort, EcWindowAccess))
                {
                    return false;
                }
                byte data = 0;
                if (!ReadPort(EcIndexLowPort, out data))
                {
                    return false;
                }
                value = data;
                return true;
            }
        }

        /// <summary>写一个 EC RAM 字节（16 位索引，走 0x2E/0x2F 窗口）。</summary>
        internal static bool TryWriteByte(int address, byte value)
        {
            if (!IsAllowed(address) || !EnsureReady())
            {
                return false;
            }
            lock (Gate)
            {
                if (!SelectWindow(address))
                {
                    return false;
                }
                if (!WritePort(EcIndexHighPort, EcWindowAccess))
                {
                    return false;
                }
                return WritePort(EcIndexLowPort, value);
            }
        }

        /// <summary>清掉"不可用"粘滞状态，允许下次重新尝试加载驱动（手动恢复入口调用）。</summary>
        internal static void ResetAvailability()
        {
            lock (Gate)
            {
                _unavailable = false;
            }
        }

        /// <summary>卸载服务并释放 DLL（探针/退出时调用；正常使用无需手动卸载）。</summary>
        internal static void Shutdown()
        {
            lock (Gate)
            {
                try
                {
                    _shutdown?.Invoke();
                }
                catch
                {
                }
                _ready = false;
                _initialize = null;
                _shutdown = null;
                _getPort = null;
                _setPort = null;
                if (_dll != IntPtr.Zero)
                {
                    try
                    {
                        FreeLibrary(_dll);
                    }
                    catch
                    {
                    }
                    _dll = IntPtr.Zero;
                }
                if (_loadedService.Length > 0)
                {
                    try
                    {
                        UnloadService(_loadedService);
                    }
                    catch
                    {
                    }
                    _loadedService = string.Empty;
                }
            }
        }

        /// <summary>
        /// 只读探针：读接管掩码与两路目标，用结果判断这台机器能否走厂商通道。
        /// <para>只读三个字节后立刻卸载服务，不写任何 EC。</para>
        /// </summary>
        internal static string RunReadOnlyProbe()
        {
            var sb = new System.Text.StringBuilder();
            bool ok = EnsureReady();
            sb.Append("厂商EC通道=").Append(ok ? "可用" : "不可用");
            if (ok)
            {
                sb.Append(" · 0xB20=").Append(Format(TryReadByte(0xB20, out byte mask), mask));
                sb.Append(" · 0xC83C=").Append(Format(TryReadByte(0xC83C, out byte fan1), fan1));
                sb.Append(" · 0xC83D=").Append(Format(TryReadByte(0xC83D, out byte fan2), fan2));
            }
            Shutdown();
            return sb.ToString();
        }

        private static string Format(bool read, byte value)
        {
            return read ? "0x" + value.ToString("X2") : "读不到";
        }

        private static bool SelectWindow(int address)
        {
            byte high = (byte)((address >> 8) & 0xFF);
            byte low = (byte)(address & 0xFF);
            return WritePort(EcIndexHighPort, EcWindowSelectHigh)
                && WritePort(EcIndexLowPort, high)
                && WritePort(EcIndexHighPort, EcWindowSelectLow)
                && WritePort(EcIndexLowPort, low);
        }

        private static bool ReadPort(int port, out byte value)
        {
            byte local = 0;
            bool ok = _getPort != null && _getPort((ushort)port, ref local, 1);
            value = local;
            return ok;
        }

        private static bool WritePort(int port, byte value)
        {
            return _setPort != null && _setPort((ushort)port, value, 1);
        }

        /// <summary>
        /// 驱动随程序内嵌：首次使用时释放到公共程序数据目录，再从那里加载。
        /// <para>这样安装包只需要主程序与硬件层两个文件，不需要额外分发驱动文件。</para>
        /// </summary>
        private static string? ExtractEmbeddedDriver()
        {
            try
            {
                string target = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MechrevoLite", "driver", "bld");
                Directory.CreateDirectory(target);
                string sysPath = Path.Combine(target, "bldring64.sys");
                string dllPath = Path.Combine(target, "Bldring64.dll");
                if (!ExtractResource("Drivers.bldring64.sys", sysPath) || !ExtractResource("Drivers.Bldring64.dll", dllPath))
                {
                    return null;
                }
                return target;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[fan] 驱动释放失败：" + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private static bool ExtractResource(string resourceName, string targetPath)
        {
            using Stream? source = typeof(BaolongdaVendorEc).Assembly.GetManifestResourceStream(resourceName);
            if (source == null)
            {
                return false;
            }
            // 先算内嵌副本的指纹，再和磁盘文件比对：只比长度会被"同长度不同内容"的文件骗过。
            byte[] expected = System.Security.Cryptography.SHA256.HashData(source);
            source.Position = 0;
            if (File.Exists(targetPath))
            {
                try
                {
                    using FileStream existing = File.OpenRead(targetPath);
                    byte[] actual = System.Security.Cryptography.SHA256.HashData(existing);
                    if (System.Linq.Enumerable.SequenceEqual(expected, actual))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("[fan] 驱动指纹校验失败（将重新释放）：" + ex.GetType().Name);
                }
            }
            // 不一致或缺失 ⇒ 无条件原子覆盖（临时文件 + 替换）。
            string temp = targetPath + ".tmp";
            using (FileStream writer = File.Create(temp))
            {
                source.CopyTo(writer);
            }
            File.Move(temp, targetPath, overwrite: true);
            return true;
        }

        private static bool TryLoadDriver(out string detail)
        {
            detail = string.Empty;
            string baseDir = AppContext.BaseDirectory;
            string? extracted = ExtractEmbeddedDriver();
            string[] candidates =
            {
                extracted ?? string.Empty,
                Path.Combine(baseDir, "Drivers", "Blding"),
                baseDir,
                Path.Combine(baseDir, "GCU", "payload", "1020", "UniwillService", "MyControlCenter")
            };
            string? dllPath = null;
            string? sysPath = null;
            string dllName = DllName;
            foreach (string dir in candidates)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                string tryDll = Path.Combine(dir, DllName);
                string trySys = Path.Combine(dir, "JiaoLongDriver64.sys");
                if (File.Exists(tryDll) && File.Exists(trySys))
                {
                    dllPath = tryDll;
                    sysPath = trySys;
                    break;
                }
                string legacyDll = Path.Combine(dir, LegacyDllName);
                string legacySys = Path.Combine(dir, "bldring64.sys");
                if (File.Exists(legacyDll) && File.Exists(legacySys))
                {
                    dllPath = legacyDll;
                    sysPath = legacySys;
                    dllName = LegacyDllName;
                    break;
                }
            }
            if (dllPath == null || sysPath == null)
            {
                detail = "未找到厂商驱动文件（JiaoLongDriver64.dll/.sys 或 Bldring64.dll/bldring64.sys）";
                return false;
            }

            string service = dllName == DllName ? ServiceName : LegacyServiceName;
            if (!LoadService(service, sysPath, out string serviceDetail, out bool serviceCreated))
            {
                detail = "驱动服务启动失败：" + serviceDetail;
                return false;
            }
            _loadedService = service;
            _serviceCreated = serviceCreated;

            _dll = LoadLibrary(dllPath);
            if (_dll == IntPtr.Zero)
            {
                detail = "驱动 DLL 加载失败（错误码 " + Marshal.GetLastWin32Error() + "）";
                UnloadService(service);
                _loadedService = string.Empty;
                return false;
            }

            IntPtr initPtr = GetProcAddress(_dll, "InitializeBldring");
            IntPtr shutdownPtr = GetProcAddress(_dll, "ShutdownBldring");
            IntPtr getPtr = GetProcAddress(_dll, "GetBLDPortVal");
            IntPtr setPtr = GetProcAddress(_dll, "SetBLDPortVal");
            if (initPtr == IntPtr.Zero || getPtr == IntPtr.Zero || setPtr == IntPtr.Zero)
            {
                detail = "驱动 DLL 缺少导出函数（InitializeBldring/GetBLDPortVal/SetBLDPortVal）";
                FreeLibrary(_dll);
                _dll = IntPtr.Zero;
                UnloadService(service);
                _loadedService = string.Empty;
                return false;
            }

            _initialize = Marshal.GetDelegateForFunctionPointer<InitializeBldringDelegate>(initPtr);
            _getPort = Marshal.GetDelegateForFunctionPointer<GetPortValDelegate>(getPtr);
            _setPort = Marshal.GetDelegateForFunctionPointer<SetPortValDelegate>(setPtr);
            if (shutdownPtr != IntPtr.Zero)
            {
                _shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownBldringDelegate>(shutdownPtr);
            }
            if (!_initialize())
            {
                detail = "驱动 DLL 初始化返回失败";
                Shutdown();
                return false;
            }
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static bool LoadService(string serviceName, string sysPath, out string detail, out bool created)
        {
            detail = string.Empty;
            created = false;
            IntPtr scm = OpenSCManager(null, null, ScManagerAllAccess);
            if (scm == IntPtr.Zero)
            {
                detail = "打开服务管理器失败（需要管理员权限）";
                return false;
            }
            try
            {
                IntPtr service = OpenService(scm, serviceName, ServiceAllAccess);
                if (service != IntPtr.Zero)
                {
                    ChangeServiceConfig(service, ServiceNoChange, ServiceDemandStart, ServiceNoChange,
                        null, null, IntPtr.Zero, null, null, null, null);
                }
                else
                {
                    service = CreateService(scm, serviceName, serviceName, ServiceAllAccess, ServiceKernelDriver,
                        ServiceDemandStart, ServiceErrorNormal, sysPath, null, IntPtr.Zero, null, null, null, null);
                    if (service == IntPtr.Zero)
                    {
                        detail = "创建设备服务失败（错误码 " + Marshal.GetLastWin32Error() + "）";
                        return false;
                    }
                }
                try
                {
                    if (StartService(service, 0, IntPtr.Zero))
                    {
                        return true;
                    }
                    int err = Marshal.GetLastWin32Error();
                    if (err == 1056)
                    {
                        return true;
                    }
                    if (err == 1275)
                    {
                        detail = "驱动签名被拦截（Windows 内存完整性/驱动签名策略），需先关闭该项或改用其它通道";
                        return false;
                    }
                    if (err == 577)
                    {
                        detail = "驱动签名无效或已被系统撤销（关闭内存完整性后再试，或改用 PawnIO）";
                        return false;
                    }
                    if (err == 5)
                    {
                        detail = "启动驱动服务被拒绝访问（需要管理员权限）";
                        return false;
                    }
                    if (err == 1073)
                    {
                        detail = "驱动服务已存在但无法启动（可能被其它程序占用）";
                        return false;
                    }
                    if (err == 1058)
                    {
                        detail = "驱动服务被系统禁用";
                        return false;
                    }
                    detail = "启动驱动服务失败（错误码 " + err + "）";
                    return false;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        private static void UnloadService(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerAllAccess);
            if (scm == IntPtr.Zero)
            {
                return;
            }
            try
            {
                IntPtr service = OpenService(scm, serviceName, ServiceAllAccess);
                if (service == IntPtr.Zero)
                {
                    return;
                }
                try
                {
                    ServiceStatus status = default;
                    ControlService(service, ServiceControlStop, ref status);
                    DeleteService(service);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
    }
}
