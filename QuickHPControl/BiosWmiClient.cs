using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;

namespace QuickHPControl;

/// <summary>
/// 直连 BIOS 的热控模式客户端 —— 不注入、不依赖 HP 的任何 DLL。
///
/// 通道：root\WMI 命名空间下的 hpqBIntM 实例（ACPI PNP0C14），
/// 调用 hpqBIOSInt&lt;N&gt; 方法，N = 返回缓冲区大小，合法值仅
/// {0, 4, 128, 1024, 4096, 8192, 16384, 32768}。
///
///   读模式   hpqBIOSInt4    Command=1 CommandType=76 Size=0                      -> Data[0] = 模式值
///   写模式   hpqBIOSInt0    Command=2 CommandType=76 Size=4 hpqBData={mode,0,0,0}
///   能力块   hpqBIOSInt128  Command=1 CommandType=13                            -> Data[6]=支持位掩码, Data[8]&amp;1=版本位
///
/// 入参 hpqBDataIn 的 Sign 必须恰为 ASCII "SECU"（53 45 43 55），否则 rwReturnCode=3。
/// hpqBDataIn 的 InstanceName 是 @key/@read 属性，只能用 ManagementClass.CreateInstance() 构造。
///
/// 需要管理员权限（High 完整性级别）才能枚举 hpqBIntM 实例。
/// 公开 API 与旧的 IpcClient 完全对齐，UI 层可无缝替换。
/// </summary>
public class BiosWmiClient : IDisposable
{
    private const string WMI_NAMESPACE = @"root\WMI";
    private const string BIOS_CLASS = "hpqBIntM";
    private const string DATA_IN_CLASS = "hpqBDataIn";

    private const uint CMD_READ = 1;
    private const uint CMD_WRITE = 2;
    private const uint CT_SUPPORTED_MODES = 13;
    private const uint CT_THERMAL_MODE = 76;

    // ---- 风扇指令码 ----
    // 来源：OmenMon (GPL3) Hardware/BiosCtl.cs 的 HP BIOS WMI 实现。
    // 该实现用 Command=0x20008（Cmd.Default）配这些 CommandType；
    // 本机 HP DLL 用的是 Command=1(读)/2(写)。哪套有效运行时探测。
    private const uint CMD_FAN_DEFAULT = 0x20008;
    private const uint CT_FAN_COUNT = 0x10;      // 读：风扇数量        returnSize=4
    private const uint CT_FAN_TYPE = 0x2C;       // 读：风扇类型        returnSize=128
    private const uint CT_FAN_LEVEL_GET = 0x2D;  // 读：转速级别        returnSize=128
    private const uint CT_FAN_LEVEL_SET = 0x2E;  // 写：转速级别        inData={fan1,fan2,0,0}
    private const uint CT_MAX_FAN_GET = 0x26;    // 读：最大转速开关    returnSize=4
    private const uint CT_MAX_FAN_SET = 0x27;    // 写：最大转速开关    inData={on,0,0,0}
    private const uint CT_SYSTEM_DATA = 0x28;    // 读：系统能力块      returnSize=128

    /// <summary>转速级别的观测上限（OmenMon 实测 0x00、0x15…0x37）。</summary>
    public const int FAN_LEVEL_MAX = 0x37;

    private const int READ_RETRY = 3;
    private const int READ_RETRY_DELAY_MS = 120;

    /// <summary>
    /// 单次 WMI 调用的超时。BIOS 通道正常情况下 1~20ms 返回；
    /// 卡住通常是 ACPI 子系统出问题，超时比无限等待更好（唤醒后会重连）。
    /// </summary>
    private static readonly TimeSpan WMI_TIMEOUT = TimeSpan.FromSeconds(5);

    private static readonly byte[] SIGN_SECU = { 0x53, 0x45, 0x43, 0x55 };

    /// <summary>能力位掩码 bit → 模式值（HP 官方 modeOrderMap，已双向验证）。</summary>
    private static readonly Dictionary<int, int> BitToMode = new Dictionary<int, int>
    {
        { 1, 5 }, { 2, 1 }, { 3, 0 }, { 4, 2 }, { 5, 3 }, { 6, 4 }, { 7, 6 }
    };

    private readonly object _lock = new object();

    private ManagementScope _scope;
    private ManagementObject _target;
    private List<int> _supportedModes = new List<int>();
    private int _version;
    private string _lastError;

    // UI 线程只读这个轻量状态，绝不为了判断连接状态等待 WMI 锁。
    // WMI 调用/Dispose 可能卡住数秒，锁上的 IsConnected 会把 Dispatcher 一起卡死。
    private int _connectedState;

    // 风扇：_fanCommand == 0 表示本机 BIOS 不暴露风扇指令
    private uint _fanCommand;
    private int _fanCount;

    public Action<string> LogCallback { get; set; }

    /// <summary>本机 BIOS 是否支持风扇指令（连接时自动探测）。</summary>
    public bool IsFanSupported => _fanCommand != 0;

    /// <summary>风扇数量（不支持时为 0）。</summary>
    public int FanCount => _fanCount;

    public bool IsConnected => Volatile.Read(ref _connectedState) != 0;

    /// <summary>上次操作的错误信息（成功时为 null）。</summary>
    public string GetLastError() => _lastError;

    private void Log(string msg) => LogCallback?.Invoke(msg);

    // ================================================================
    //  连接
    // ================================================================

    public bool Connect()
    {
        lock (_lock)
        {
            DisconnectInternal();
            _lastError = null;

            try
            {
                var opts = new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                    Timeout = WMI_TIMEOUT
                };
                var scope = new ManagementScope(@"\\.\" + WMI_NAMESPACE, opts);
                scope.Connect();

                ManagementObject target = null;
                var enumOpts = new EnumerationOptions { Timeout = WMI_TIMEOUT, ReturnImmediately = false };
                using (var mc = new ManagementClass(scope, new ManagementPath(BIOS_CLASS), null))
                {
                    foreach (ManagementObject o in mc.GetInstances(enumOpts))
                    {
                        target = o;
                        break;
                    }
                }

                if (target == null)
                {
                    _lastError = "未找到 " + BIOS_CLASS + " 实例";
                    Log("未找到 BIOS 接口实例，请确认以管理员身份运行");
                    return false;
                }

                _scope = scope;
                _target = target;

                string instanceName = "";
                try { instanceName = Convert.ToString(target["InstanceName"]); } catch { }
                Log("已连接 BIOS 通道 " + instanceName);

                _version = ReadVersionInternal();
                if (_version == 0)
                {
                    // 读不到版本位时按 V1 处理
                    _version = 1;
                }

                _supportedModes = ReadSupportedModesInternal();
                if (_supportedModes.Count == 0)
                {
                    Log("未读到支持模式列表，回退为全部 7 种模式");
                    _supportedModes = new List<int> { 5, 1, 0, 2, 3, 4, 6 };
                }

                Log("系统版本 V" + _version + "，支持 " + _supportedModes.Count + " 个模式");

                // 本机 BIOS 已通过只读取证确认不暴露风扇控制指令（所有候选命令
                // 返回 rc=3）。禁止每次连接/唤醒时再次探测：未知 CommandType 里
                // 混有写指令，且 ACPI 忙时每次探测可能等待 WMI 超时，正是长时间
                // 未响应的放大器。
                _fanCommand = 0;
                _fanCount = 0;
                Log("本机 BIOS 未暴露风扇控制指令，风扇功能已禁用");

                // 只有连接流程全部完成后才公开「已连接」，避免 UI/看门狗在此期间
                // 误以为连接已就绪并访问仍在初始化的 WMI 对象。
                Volatile.Write(ref _connectedState, 1);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log("连接 BIOS 通道失败: " + ex.Message);
                DisconnectInternal();
                return false;
            }
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectInternal();
        }
    }

    private void DisconnectInternal()
    {
        // 先让 UI/看门狗看到「已断开」，再释放 WMI 对象；Dispose 可能阻塞。
        Volatile.Write(ref _connectedState, 0);
        try { _target?.Dispose(); } catch { }
        _target = null;
        _scope = null;
        _supportedModes = new List<int>();
        _version = 0;
        _fanCommand = 0;
        _fanCount = 0;
    }

    public void Dispose() => Disconnect();

    // ================================================================
    //  查询
    // ================================================================

    /// <summary>读当前热控模式（BIOS 真值）。失败返回 -1。</summary>
    private int GetCurrentModeInternal()
    {
        if (_target == null)
        {
            _lastError = "未连接";
            return -1;
        }

        if (!ReadModeRaw(out int rc, out byte[] data, out string err))
        {
            _lastError = err;
            return -1;
        }
        if (rc != 0)
        {
            _lastError = "BIOS 返回码 " + rc;
            return -1;
        }
        if (data == null || data.Length < 1)
        {
            _lastError = "返回数据为空";
            return -1;
        }

        _lastError = null;
        return data[0];
    }

    public List<int> GetSupportedModes()
    {
        // _supportedModes 只整体替换、不原地修改；直接取快照，避免 UI 线程等 WMI 锁。
        List<int> snapshot = Volatile.Read(ref _supportedModes);
        return snapshot == null ? new List<int>() : new List<int>(snapshot);
    }

    /// <summary>
    /// 一次性取回界面需要的全部状态，避免 UI 层为了拼状态反复进锁。
    /// 返回串格式（沿用旧 IPC 协议，UI 按 key 取值）：
    ///   MODE:&lt;模式&gt;|NAME:&lt;名称&gt;|POWER:&lt;电源&gt;|VERSION:&lt;Vx&gt;|SLIDER:&lt;滑块&gt;|ESAVER:&lt;节能&gt;|FAN:&lt;风扇&gt;|READY:&lt;bool&gt;
    /// </summary>
    public string GetStatus()
    {
        lock (_lock)
        {
            int mode = GetCurrentModeInternal();
            bool onBattery = PowerPlanManager.IsOnBattery();

            return "MODE:" + mode
                 + "|NAME:" + (mode < 0 ? "未知" : MainWindow.GetModeName(mode))
                 + "|POWER:" + (onBattery ? "电池" : "交流电")
                 + "|VERSION:V" + _version
                 + "|SLIDER:" + PowerPlanManager.GetSliderName()
                 + "|ESAVER:" + EnergySaverManager.Describe(EnergySaverManager.GetState())
                 + "|FAN:" + (_fanCommand != 0 ? _fanCount.ToString() : "无")
                 + "|READY:" + (mode >= 0);
        }
    }

    // ================================================================
    //  写入
    // ================================================================

    /// <summary>
    /// 设置热控模式。
    ///
    /// ★ 顺序至关重要，四步必须在同一把锁内、无延迟地连续完成：
    ///   ① 写 HP 的 AppData（SelectedBalancedSerices + triggerSource=UserAction）
    ///   ② 写 BIOS
    ///   ③ 同步 Windows 电源模式滑块
    ///   ④ 按需同步 Windows 节能模式
    ///
    /// 为什么 ① 必须最先：HP 的 HP.SystemControl.Background.exe 监听电源模式变化，
    /// 一旦看到「平衡」而 triggerSource 还是 Other(-1)，就会把 SelectedBalancedSerices
    /// 改回 SmartSense 并写回 BIOS —— 用户看到的现象就是「切到最冷/平衡/安静/静音后
    /// 自己跳回智能模式」。先把 triggerSource 声明成 UserAction，HP 就会尊重我们的选择。
    ///
    /// 为什么不能有延迟：HP 通过 Windows 消息泵异步收到电源模式通知，
    /// 只要我们在它处理之前把状态全部写完，它读到的就是一致状态。
    /// </summary>
    public bool SetMode(int mode)
    {
        lock (_lock)
        {
            if (_target == null)
            {
                _lastError = "未连接";
                return false;
            }
            if (_supportedModes.Count > 0 && !_supportedModes.Contains(mode))
            {
                _lastError = "模式 " + mode + " 不被本机支持";
                return false;
            }

            // 写之前先读一次当前值，用于判断是否真的发生了模式切换
            int previous = GetCurrentModeInternal();

            // ① 先声明「这是用户主动切的」，压住 HP 后台服务
            PublishToHp(mode);

            // ② 写 BIOS
            if (!WriteModeRaw(mode, out int rc, out string err))
            {
                _lastError = err;
                return false;
            }
            if (rc != 0)
            {
                _lastError = "BIOS 返回码 " + rc;
                return false;
            }

            _lastError = null;

            // ③ 同步 Windows 电源模式滑块：让 HP 后台服务看到的各路信号保持一致，
            //    否则它可能按滑块的档位把 BIOS 模式改回去。
            try
            {
                bool onBattery = PowerPlanManager.IsOnBattery();
                if (PowerPlanManager.SetForMode(mode, onBattery))
                {
                    Log("已同步电源模式滑块 -> " + PowerPlanManager.GetSliderName());
                }
                else
                {
                    Log("同步电源模式滑块失败（不影响 BIOS 模式已生效）");
                }
            }
            catch (Exception ex)
            {
                Log("同步电源模式滑块异常: " + ex.Message);
            }

            // ④ 节能模式只在「模式真正切换」时同步一次，之后用户手动改回来也不会被覆盖。
            if (previous != mode)
            {
                SyncEnergySaver(mode);
            }
            else
            {
                Log("模式未变化，跳过节能模式同步");
            }

            return true;
        }
    }

    /// <summary>
    /// 把「用户切到了 mode」写进 HP 自己的设置存储（best-effort）。
    /// 本机没装 HP、或存储布局不认识时静默跳过 —— BIOS 模式已经生效，不受影响。
    /// </summary>
    private void PublishToHp(int mode)
    {
        try
        {
            if (!HpSystemControlBridge.IsAvailable)
            {
                return;
            }

            if (HpSystemControlBridge.PublishUserMode(mode))
            {
                Log("已同步 HP 后台服务状态（triggerSource=UserAction, 平衡系列=" + mode + "）");
            }
            else
            {
                Log("同步 HP 后台服务状态失败: " + (HpSystemControlBridge.GetLastError() ?? "未知错误"));
            }
        }
        catch (Exception ex)
        {
            Log("同步 HP 后台服务状态异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 性能模式(0) → 关闭 Windows 节能模式；其他模式 → 不操作。
    /// 省电模式(4) → 打开 Windows 节能模式；其他模式 → 不操作。
    /// 只在模式切换的瞬间调用一次，不做持续强制。
    /// </summary>
    private void SyncEnergySaver(int mode)
    {
        if (mode != 4 && mode != 0) return;
        bool want = mode == 4;

        try
        {
            int current = EnergySaverManager.GetState();
            if (current < 0)
            {
                Log("节能模式状态读取失败，跳过同步");
                return;
            }

            int target = want ? EnergySaverManager.STATE_ON : EnergySaverManager.STATE_OFF;
            if (current == target)
            {
                Log("Windows 节能模式已是「" + EnergySaverManager.Describe(current) + "」，无需修改");
                return;
            }

            if (SetEnergySaver(want, out string err))
            {
                Log("已同步 Windows 节能模式 -> " + (want ? "开" : "关"));
            }
            else
            {
                Log("同步 Windows 节能模式失败: " + err);
            }
        }
        catch (Exception ex)
        {
            Log("同步 Windows 节能模式异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 独立设置 Windows 节能模式（不影响热控模式），并把结果同步给 HP 的 IPC 记录。
    /// 走 WNF 广播，普通用户态即可，不需要管理员权限。
    /// </summary>
    public bool SetEnergySaver(bool on, out string error)
    {
        if (!EnergySaverManager.SetEnabled(on, out error))
        {
            return false;
        }

        try
        {
            HpSystemControlBridge.PublishEnergySaver(on);
        }
        catch
        {
            // 只是给 HP 看的镜像状态，失败无所谓
        }

        return true;
    }

    // ================================================================
    //  风扇
    //
    //  指令码来源：OmenMon (GPL3) 的 Hardware/BiosCtl.cs。
    //  本机是否真的支持，靠 DetectFanSupport() 运行时探测 ——
    //  探不到就整体禁用，UI 不显示风扇区块。
    //
    //  注意：风扇「实际转速 RPM / 百分比」走的是 EC（嵌入式控制器），
    //  需要内核驱动，本程序不碰。这里能读到的只是 BIOS 的「转速级别」。
    // ================================================================

    /// <summary>
    /// 探测风扇指令是否可用。两种 Command 编码都试一遍，
    /// 以「能读出 1~8 之间的风扇数量」作为判定依据。
    /// </summary>
    private bool DetectFanSupport()
    {
        _fanCommand = 0;
        _fanCount = 0;

        foreach (uint cmd in new[] { CMD_FAN_DEFAULT, CMD_READ })
        {
            if (!TryRaw("hpqBIOSInt4", cmd, CT_FAN_COUNT, new byte[4], SIGN_SECU, 4,
                        out int rc, out byte[] d, out _))
                continue;
            if (rc != 0 || d == null || d.Length < 1)
                continue;
            if (d[0] < 1 || d[0] > 8)
                continue;

            _fanCommand = cmd;
            _fanCount = d[0];
            return true;
        }
        return false;
    }

    /// <summary>读每个风扇的当前转速级别（原始档位值）。失败返回 null。</summary>
    public int[] GetFanLevels()
    {
        lock (_lock)
        {
            if (_fanCommand == 0) { _lastError = "本机不支持风扇控制"; return null; }

            if (!TryRaw("hpqBIOSInt128", _fanCommand, CT_FAN_LEVEL_GET, new byte[4], SIGN_SECU, 4,
                        out int rc, out byte[] d, out string err) || rc != 0 || d == null)
            {
                _lastError = err ?? ("BIOS 返回码 " + rc);
                return null;
            }

            int n = Math.Min(_fanCount, d.Length);
            var levels = new int[n];
            for (int i = 0; i < n; i++)
            {
                levels[i] = d[i];
            }
            _lastError = null;
            return levels;
        }
    }

    /// <summary>
    /// 设置每个风扇的转速级别。levels 按风扇顺序给出（最多 2 个）。
    /// 部分机型需要先经 EC 打开「手动风扇」才生效，本程序不做这一步，
    /// 因此 BIOS 返回码为 0 也可能实际未生效。
    /// </summary>
    public bool SetFanLevels(int[] levels)
    {
        lock (_lock)
        {
            if (_fanCommand == 0) { _lastError = "本机不支持风扇控制"; return false; }
            if (levels == null || levels.Length == 0) { _lastError = "参数为空"; return false; }

            byte[] payload = { 0, 0, 0, 0 };
            for (int i = 0; i < levels.Length && i < 2; i++)
            {
                payload[i] = (byte)Math.Max(0, Math.Min(255, levels[i]));
            }

            if (!TryRaw("hpqBIOSInt0", _fanCommand, CT_FAN_LEVEL_SET, payload, SIGN_SECU, 4,
                        out int rc, out _, out string err))
            {
                _lastError = err;
                return false;
            }
            if (rc != 0)
            {
                _lastError = "BIOS 返回码 " + rc;
                return false;
            }
            _lastError = null;
            return true;
        }
    }

    /// <summary>读「最大转速」开关状态。失败返回 false 并置 _lastError。</summary>
    public bool GetMaxFan(out bool on)
    {
        on = false;
        lock (_lock)
        {
            if (_fanCommand == 0) { _lastError = "本机不支持风扇控制"; return false; }

            if (!TryRaw("hpqBIOSInt4", _fanCommand, CT_MAX_FAN_GET, new byte[4], SIGN_SECU, 4,
                        out int rc, out byte[] d, out string err) || rc != 0 || d == null || d.Length < 1)
            {
                _lastError = err ?? ("BIOS 返回码 " + rc);
                return false;
            }
            on = (d[0] & 0x01) != 0;
            _lastError = null;
            return true;
        }
    }

    /// <summary>开/关「最大转速」。</summary>
    public bool SetMaxFan(bool on)
    {
        lock (_lock)
        {
            if (_fanCommand == 0) { _lastError = "本机不支持风扇控制"; return false; }

            byte[] payload = { on ? (byte)0x01 : (byte)0x00, 0, 0, 0 };
            if (!TryRaw("hpqBIOSInt0", _fanCommand, CT_MAX_FAN_SET, payload, SIGN_SECU, 4,
                        out int rc, out _, out string err))
            {
                _lastError = err;
                return false;
            }
            if (rc != 0)
            {
                _lastError = "BIOS 返回码 " + rc;
                return false;
            }
            _lastError = null;
            return true;
        }
    }

    /// <summary>
    /// 读风扇类型。每半字节一个风扇：1=CPU, 2=GPU, 3=排气, 4=水泵, 5=进气。
    /// 失败返回 null。
    /// </summary>
    public int[] GetFanTypes()
    {
        lock (_lock)
        {
            if (_fanCommand == 0) return null;

            if (!TryRaw("hpqBIOSInt128", _fanCommand, CT_FAN_TYPE, new byte[4], SIGN_SECU, 4,
                        out int rc, out byte[] d, out _) || rc != 0 || d == null || d.Length < 1)
                return null;

            var types = new int[_fanCount];
            for (int i = 0; i < _fanCount; i++)
            {
                types[i] = i == 0 ? (d[0] & 0x0F) : ((d[0] >> 4) & 0x0F);
            }
            return types;
        }
    }

    /// <summary>风扇类型编号 → 中文名。</summary>
    public static string FanTypeName(int type)
    {
        switch (type)
        {
            case 1: return "CPU 风扇";
            case 2: return "GPU 风扇";
            case 3: return "排气风扇";
            case 4: return "水泵";
            case 5: return "进气风扇";
            default: return "风扇";
        }
    }

    // ================================================================
    //  WMI 原始调用
    // ================================================================

    /// <summary>读模式：cmd=1 ct=76 returnSize=4，BIOS 偶发忙，带重试。</summary>
    private bool ReadModeRaw(out int rc, out byte[] data, out string err)
    {
        rc = -1;
        data = null;
        err = null;

        for (int attempt = 0; attempt < READ_RETRY; attempt++)
        {
            if (TryRaw("hpqBIOSInt4", CMD_READ, CT_THERMAL_MODE, new byte[0], SIGN_SECU, 0,
                       out rc, out data, out err))
            {
                return true;
            }
            Thread.Sleep(READ_RETRY_DELAY_MS);
        }
        return false;
    }

    /// <summary>写模式：cmd=2 ct=76 data={mode,0,0,0}，主用 hpqBIOSInt0，失败退到 hpqBIOSInt4。</summary>
    private bool WriteModeRaw(int mode, out int rc, out string err)
    {
        byte[] d = { (byte)mode, 0, 0, 0 };
        byte[] ignored;

        if (TryRaw("hpqBIOSInt0", CMD_WRITE, CT_THERMAL_MODE, d, SIGN_SECU, 4,
                   out rc, out ignored, out err))
        {
            return true;
        }

        string e1 = err;
        if (TryRaw("hpqBIOSInt4", CMD_WRITE, CT_THERMAL_MODE, d, SIGN_SECU, 4,
                   out rc, out ignored, out err))
        {
            return true;
        }

        err = "hpqBIOSInt0 -> " + e1 + " | hpqBIOSInt4 -> " + err;
        return false;
    }

    /// <summary>读 128 字节能力块（cmd=1 ct=13）。</summary>
    private byte[] ReadSupportBlock()
    {
        if (_target == null) return null;
        if (!TryRaw("hpqBIOSInt128", CMD_READ, CT_SUPPORTED_MODES, new byte[0], SIGN_SECU, 0,
                    out int rc, out byte[] data, out _))
        {
            return null;
        }
        return rc == 0 ? data : null;
    }

    /// <summary>Data[8] &amp; 1：0=V1, 1=V2。读不到返回 0。</summary>
    private int ReadVersionInternal()
    {
        byte[] d = ReadSupportBlock();
        if (d == null || d.Length < 9) return 0;
        return (d[8] & 1) == 0 ? 1 : 2;
    }

    /// <summary>Data[6] 是支持位掩码，按 BitToMode 解出模式值列表。</summary>
    private List<int> ReadSupportedModesInternal()
    {
        var list = new List<int>();
        byte[] d = ReadSupportBlock();
        if (d == null || d.Length < 7) return list;

        byte mask = d[6];
        foreach (var kv in BitToMode)
        {
            if ((mask & (1 << kv.Key)) != 0)
            {
                list.Add(kv.Value);
            }
        }
        return list;
    }

    /// <summary>
    /// 单次原始调用。hpqBDataIn 是带 @key/@read 属性的模板类，
    /// 必须用 ManagementClass.CreateInstance() 构造，不能 new ManagementObject(path)。
    /// </summary>
    private bool TryRaw(string method, uint command, uint commandType, byte[] data, byte[] sign, int size,
                        out int rc, out byte[] outData, out string err)
    {
        rc = -1;
        outData = null;
        err = null;

        if (_target == null || _scope == null)
        {
            err = "未连接";
            return false;
        }

        try
        {
            ManagementBaseObject inData;
            using (var dc = new ManagementClass(_scope, new ManagementPath(DATA_IN_CLASS), null))
            {
                inData = dc.CreateInstance();
            }

            inData["Command"] = command;
            inData["CommandType"] = commandType;
            inData["Size"] = (uint)size;
            if (data != null) inData["hpqBData"] = data;
            if (sign != null) inData["Sign"] = sign;

            ManagementBaseObject inParams = _target.GetMethodParameters(method);
            inParams["InData"] = inData;

            // 关键：不能传 null。某些 ACPI/WMI 调用在固件忙或唤醒后会一直等，
            // 没有 InvokeMethodOptions.Timeout 时，后台任务会长期占住 _lock，
            // 轮询/重连不断排队，最终表现为程序约半分钟后才恢复。
            var invokeOpts = new InvokeMethodOptions
            {
                Timeout = WMI_TIMEOUT,
                Context = null
            };
            ManagementBaseObject outParams = _target.InvokeMethod(method, inParams, invokeOpts);
            var mo = outParams["OutData"] as ManagementBaseObject;
            if (mo == null)
            {
                err = "OutData 为空";
                return false;
            }

            rc = Convert.ToInt32(mo["rwReturnCode"]);
            outData = mo["Data"] as byte[];
            return true;
        }
        catch (Exception ex)
        {
            err = ex.GetType().Name + ": " + ex.Message.Trim();
            return false;
        }
    }
}
