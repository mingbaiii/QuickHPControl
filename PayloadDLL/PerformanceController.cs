using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HP.SystemControl.AppData;
using HP.SystemControl.Utility.Features;

namespace Payload
{
    public class PerformanceController : INotifyPropertyChanged
    {
        // SystemControlMode 枚举值: Performance=0, Balanced=1, Cool=2, Quiet=3, PowerSaver=4, SmartSense=5, Silent=6
        public static readonly Dictionary<int, string> ModeNames = new Dictionary<int, string>
        {
            { 0, "Performance" }, { 1, "Balanced" }, { 2, "Cool" },
            { 3, "Quiet" }, { 4, "PowerSaver" }, { 5, "SmartSense" }, { 6, "Silent" }
        };

        public static readonly Dictionary<int, string> ModeDescriptions = new Dictionary<int, string>
        {
            { 0, "高性能模式，释放全部性能" },
            { 1, "标准平衡模式" },
            { 2, "凉爽模式，降低温度" },
            { 3, "安静模式，减少噪音" },
            { 4, "省电模式，延长续航" },
            { 5, "智能模式，自动平衡性能与功耗" },
            { 6, "静音模式，最低噪音" }
        };

        public static readonly Dictionary<int, string> ModeIcons = new Dictionary<int, string>
        {
            { 0, "" }, { 1, "" }, { 2, "" },
            { 3, "" }, { 4, "" }, { 5, "" }, { 6, "" }
        };

        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _pollCts;

        public Action<string> LogCallback { get; set; }
        private void Log(string msg) { LogCallback?.Invoke(msg); }

        // === 属性 ===
        private int _currentMode;
        public int CurrentMode
        {
            get => _currentMode;
            private set { _currentMode = value; OnPropertyChanged(); OnPropertyChanged("CurrentModeName"); OnPropertyChanged("CurrentModeDescription"); }
        }

        private bool _isOnBattery;
        public bool IsOnBattery
        {
            get => _isOnBattery;
            private set { _isOnBattery = value; OnPropertyChanged(); OnPropertyChanged("PowerStatusText"); }
        }

        private int _systemVersion;
        public int SystemVersion
        {
            get => _systemVersion;
            private set { _systemVersion = value; OnPropertyChanged(); OnPropertyChanged("VersionText"); }
        }

        private int _selectedBalancedMode = 5;  // SmartSense
        public int SelectedBalancedMode
        {
            get => _selectedBalancedMode;
            private set { _selectedBalancedMode = value; }
        }

        private List<int> _supportedModes = new List<int>();
        public List<int> SupportedModes
        {
            get => _supportedModes;
            private set { _supportedModes = value; OnPropertyChanged(); }
        }

        private bool _isInitialized;
        public bool IsInitialized
        {
            get => _isInitialized;
            private set { _isInitialized = value; OnPropertyChanged(); }
        }

        private string _statusMessage = "正在初始化...";
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set { _isError = value; OnPropertyChanged(); }
        }

        // === 计算属性 ===
        public string CurrentModeName =>
            ModeNames.TryGetValue(CurrentMode, out string name) ? name : "Unknown(" + CurrentMode + ")";

        public string CurrentModeDescription =>
            ModeDescriptions.TryGetValue(CurrentMode, out string desc) ? desc : "";

        public string PowerStatusText => IsOnBattery ? "电池供电" : "交流电源";
        public string VersionText => "V" + SystemVersion;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ================================================================
        //  电池检测 (P/Invoke 替代 System.Windows.Forms)
        // ================================================================

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        // ACLineStatus: 0=Offline(电池), 1=Online(交流), 255=Unknown
        private bool GetIsOnBattery()
        {
            if (GetSystemPowerStatus(out var sps))
                return sps.ACLineStatus == 0;
            return false;
        }

        // ================================================================
        //  初始化
        // ================================================================

        public async Task InitializeAsync()
        {
            try
            {
                Log("正在读取 BIOS 信息...");
                StatusMessage = "正在读取 BIOS 信息...";

                BiosWmiManager.LogCallback = Log;
                SystemVersion = await BiosWmiManager.GetVersion();

                if (SystemVersion == 0)
                {
                    Log("0x0D 数据读取失败，尝试直接调用 GetThermalControlMode...");
                    try
                    {
                        int directMode = BiosWmiManager.GetCurrentMode();
                        Log("GetThermalControlMode 直接返回: " + directMode);
                        if (directMode >= 0)
                        {
                            SystemVersion = 1;
                            SupportedModes = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
                            RefreshStatus();
                            IsInitialized = true;
                            StatusMessage = "就绪（通过直接模式读取）";
                            IsError = false;
                            return;
                        }
                    }
                    catch (Exception ex2)
                    {
                        Log("GetThermalControlMode 也失败: " + ex2.GetType().Name + ": " + ex2.Message);
                    }

                    Log("所有方式均失败");
                    StatusMessage = "错误：无法读取 BIOS 信息，请确认在 HP 设备上运行";
                    IsError = true;
                    return;
                }

                Log("检测到系统版本: V" + SystemVersion);

                SupportedModes = await BiosWmiManager.GetSupportedModes();
                var modeNames = SupportedModes.ConvertAll(m => GetModeName(m));
                Log("支持的模式: [" + string.Join(", ", modeNames) + "]");

                bool hasSmartSense = await BiosWmiManager.IsSupportSmartSense();
                SelectedBalancedMode = hasSmartSense ? 5 : 1;  // SmartSense=5, Balanced=1
                Log("默认平衡模式: " + GetModeName(SelectedBalancedMode));

                RefreshStatus();
                Log("当前模式: " + CurrentModeName + ", 电源: " + PowerStatusText);

                IsInitialized = true;
                StatusMessage = "就绪";
                IsError = false;
            }
            catch (Exception ex)
            {
                Log("初始化异常: " + ex.GetType().Name + ": " + ex.Message);
                StatusMessage = "初始化失败: " + ex.Message;
                IsError = true;
            }
        }

        // ================================================================
        //  状态刷新
        // ================================================================

        public void RefreshStatus()
        {
            IsOnBattery = GetIsOnBattery();

            int rawBios = BiosWmiManager.GetCurrentMode();

            if (SystemVersion == 2)
            {
                // V2: 电源计划只区分 Performance/Balanced/PowerSaver 三大类
                // Balanced 系列子模式 (SmartSense/Balanced/Cool/Quiet/Silent) 从 BIOS 读取
                if (PowerPlanManager.GetCurrent(IsOnBattery, out Guid guid))
                {
                    int ppCategory = PowerPlanManager.GetCategoryFromGuid(guid);

                    if (ppCategory == 0)  // Performance
                    {
                        CurrentMode = 0;
                    }
                    else if (ppCategory == 4)  // PowerSaver
                    {
                        CurrentMode = 4;
                    }
                    else  // Balanced series → BIOS 决定具体子模式
                    {
                        CurrentMode = rawBios;
                    }
                }
                else
                {
                    // 电源计划读取失败 → 回退到 BIOS
                    CurrentMode = rawBios;
                }
            }
            else
            {
                // V1: 完全由 BIOS 决定
                CurrentMode = rawBios;
            }
        }

        // ================================================================
        //  设置模式
        // ================================================================

        public async Task<bool> SetModeAsync(int mode)
        {
            if (!SupportedModes.Contains(mode))
            {
                StatusMessage = "模式 " + GetModeName(mode) + " 不被支持";
                IsError = true;
                return false;
            }

            await _lock.WaitAsync();
            try
            {
                StatusMessage = "正在切换到 " + GetModeName(mode) + "...";
                IsError = false;

                // ★ 关键修复 (V2): 顺序 = AppData → BIOS → 电源计划
                // 三步必须在同一锁内、无延迟地完成（<10ms），不给 HP 后台进程留下
                // 检测到中间状态的窗口。HP 的 SystemEvents.PowerModeChanged 通过
                // Windows 消息泵异步派发，在我们全部写完后才执行，此时三者一致。
                if (SystemVersion == 2 && IsBalancedSeries(mode))
                {
                    StoreModeToAppData(mode);
                }

                BiosWmiManager.SetMode(mode);

                if (SystemVersion == 2)
                {
                    Guid ppGuid = GetPowerPlanGuid(mode);
                    bool ok = PowerPlanManager.SetForMode(mode, IsOnBattery);
                    if (!ok)
                    {
                        StatusMessage = "电源计划同步失败";
                        IsError = true;
                    }
                }

                try { ElpCore.Instance.SetELP(mode == 4); }  // PowerSaver=4
                catch { }

                CurrentMode = mode;
                if (IsBalancedSeries(mode))
                    SelectedBalancedMode = mode;

                StatusMessage = "已切换到 " + GetModeName(mode);
                IsError = false;
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "切换失败: " + ex.Message;
                IsError = true;
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        // ================================================================
        //  轮询
        // ================================================================

        public void StartPolling(int intervalMs = 1000)
        {
            StopPolling();
            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        RefreshStatus();
                        await Task.Delay(intervalMs, token);
                    }
                    catch (TaskCanceledException) { break; }
                    catch { await Task.Delay(intervalMs, token); }
                }
            }, token);
        }

        public void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        // ================================================================
        //  辅助
        // ================================================================

        public static string GetModeName(int mode)
        {
            return ModeNames.TryGetValue(mode, out string name) ? name : "Unknown(" + mode + ")";
        }

        // BalancedModeSeries: SmartSense(5), Balanced(1), Cool(2), Quiet(3), Silent(6)
        private static bool IsBalancedSeries(int mode)
        {
            return mode == 5 || mode == 1 || mode == 2 || mode == 3 || mode == 6;
        }

        /// <summary>
        /// 复制 HP V2 StoreModeToAppData 逻辑 — 必须存 AppData，否则 RestoreSystemControlMode 会覆盖。
        /// ★ 同时设置 EffectivePowerModeAppData.triggerSource = UserAction，阻止 HP 的
        ///   ConvertToSystemControl(Balanced) 将 SelectedBalancedSerices 覆写为 _defaultOsBalancedMode。
        ///   如果 triggerSource == Other，HP 会在检测到电源计划变为 Balanced 时强制写回 SmartSense！
        /// </summary>
        private static void StoreModeToAppData(int mode)
        {
            try
            {
                // 1. 写入 SystemControlAppData.SelectedBalancedSerices
                SystemControlAppData storageInfo = SystemControlAppDataHelper.GetStorageInfo();
                storageInfo.SelectedBalancedSerices = (SystemControlMode)mode;
                SystemControlAppDataHelper.SetStorageInfo(storageInfo);

                // 2. ★ 设置 triggerSource = UserAction，阻止 HP ConvertToSystemControl 覆写
                //    关键！HP 的 BidirectionalSystemControl.ConvertToSystemControl() 中：
                //      if (triggerSource == Other) → SelectedBalancedSerices = _defaultOsBalancedMode (SmartSense)
                //    设成 UserAction 后，HP 会保留我们写入的 SelectedBalancedSerices (Cool/Balanced/Quiet/Silent)
                EffectivePowerModeAppData effectiveInfo = EffectivePowerModeAppDataHelper.GetStorageInfo();
                int oldTrigger = (int)effectiveInfo.triggerSource;
                effectiveInfo.triggerSource = EffectivePowerModeTriggerSource.UserAction;
                EffectivePowerModeAppDataHelper.SetStorageInfo(effectiveInfo);
            }
            catch (Exception ex)
            {
            }
        }

        private static Guid GetPowerPlanGuid(int mode)
        {
            switch (mode)
            {
                case 0: return PowerPlanManager.BEST_PERFORMANCE;
                case 4: return PowerPlanManager.BEST_POWER_EFFICIENCY;
                default: return PowerPlanManager.BALANCED;
            }
        }
    }
}
