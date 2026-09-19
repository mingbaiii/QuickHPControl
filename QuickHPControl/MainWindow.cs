using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern.Controls;

namespace QuickHPControl;

public partial class MainWindow : Window, IComponentConnector
{
    private readonly BiosWmiClient _bios = new BiosWmiClient();
    private readonly Dictionary<int, ToggleButton> _modeButtons = new Dictionary<int, ToggleButton>();
    private DispatcherTimer _pollTimer;
    private DispatcherTimer _watchdogTimer;
    private int _currentMode = -1;
    private bool _isUpdatingAutoStart;
    private bool _isUpdatingEnergySaver;
    private bool _isActuallyExiting;

    /// <summary>本机 BIOS 是否暴露风扇指令（连接时探测，决定风扇区块是否显示）。</summary>
    private bool _fanSupported;

    /// <summary>程序内部修改风扇开关时置位，避免 Toggled/ValueChanged 回环触发写入。</summary>
    private bool _isUpdatingFan;

    /// <summary>轮询重入保护：0 = 空闲，1 = 有一轮正在跑。避免慢轮询叠加。</summary>
    private int _pollBusy;

    /// <summary>连接/重连重入保护：0 = 空闲，1 = 正在进行。避免唤醒事件叠加触发。</summary>
    private int _connectBusy;

    /// <summary>上一次「成功刷完一轮状态」的时间，看门狗据此判断通道是否已经僵死。</summary>
    private DateTime _lastSuccessUtc = DateTime.UtcNow;

    /// <summary>日志框最多保留的行数，超出后砍掉前 1/4（避免文档越来越大导致重排变慢）。</summary>
    private const int LOG_MAX_LINES = 400;

    private const int POLL_INTERVAL_MS = 2000;
    private const int WATCHDOG_INTERVAL_MS = 5000;
    private const int WATCHDOG_STALE_SECONDS = 20;

    /// <summary>日志缓存行数（只增不减的计数器，用来触发裁剪）。</summary>
    private int _logLineCount;

    private const string TASK_NAME = "QuickHPControlAutoStart";

    public static readonly Dictionary<int, string> ModeIcons = new Dictionary<int, string>
    {
        { 0, "\uEC4A" },  // 性能模式
		{ 1, "\uE8E3" },  // 平衡模式
		{ 2, "\uE9CA" },  // 最冷模式
		{ 3, "\uF620" },  // 安静模式
		{ 4, "\uEA95" },  // 省电模式
		{ 5, "\uE99A" },  // 智能模式
		{ 6, "\uF620" }   // 静音模式
	};

    public static readonly Dictionary<int, string> ModeDescriptions = new Dictionary<int, string>
    {
        { 0, "高性能优先" },
        { 1, "自动平衡性能与功耗" },
        { 2, "优先降低温度" },
        { 3, "静音风扇优先" },
        { 4, "延长电池续航" },
        { 5, "自动调节性能" },
        { 6, "最低噪音运行" }
    };

    public static readonly Dictionary<int, string> ModeNames = new Dictionary<int, string>
    {
        { 0, "性能模式" },
        { 1, "平衡模式" },
        { 2, "最冷模式" },
        { 3, "安静模式" },
        { 4, "省电模式" },
        { 5, "智能模式" },
        { 6, "静音模式" }
    };

    public static string GetModeName(int mode)
    {
        if (ModeNames.TryGetValue(mode, out var name))
        {
            return name;
        }
        return "未知模式(" + mode + ")";
    }

    public MainWindow()
    {
        InitializeComponent();
        MainNavView.SelectedItem = PerformanceItem;
        _bios.LogCallback = Log;
        base.Loaded += MainWindow_Loaded;
        base.Closing += MainWindow_Closing;
        base.StateChanged += MainWindow_StateChanged;
    }

    /// <summary>
    /// 写日志。★ 必须用 BeginInvoke（异步投递）而不是 Invoke（同步等待）。
    ///
    /// 所有 WMI 调用都在后台线程上，而日志回调也在后台线程上被触发。
    /// 如果用 Invoke，只要 UI 线程当时正忙（布局、弹窗、用户拖着窗口），
    /// 后台线程就会卡在日志这一步、迟迟不放 BiosWmiClient 的锁，
    /// 于是托盘点击、模式切换全部跟着卡住 —— 这就是「偶尔未响应」的主因。
    /// </summary>
    private void Log(string message)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n";
        try
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)), DispatcherPriority.Background);
            }
        }
        catch (InvalidOperationException)
        {
            // 窗口正在退出时，丢弃最后一条日志即可，不能让后台线程崩掉。
        }
    }

    private void AppendLog(string line)
    {
        TxtLog.AppendText(line);
        _logLineCount++;

        if (_logLineCount > LOG_MAX_LINES)
        {
            // 砍掉前 1/4：用字符索引切，避免逐行操作
            int cut = TxtLog.GetCharacterIndexFromLineIndex(LOG_MAX_LINES / 4);
            if (cut > 0)
            {
                TxtLog.Text = TxtLog.Text.Substring(cut);
            }
            _logLineCount = LOG_MAX_LINES - LOG_MAX_LINES / 4;
        }

        TxtLog.ScrollToEnd();
    }

    private void UpdateConnStatus(string text, bool isError = false)
    {
        TxtConnStatusSettings.Text = text;
        TxtConnStatusSettings.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 80, 80))
            : (Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private void UpdateRunStatus(string text, bool isError = false)
    {
        TxtRunStatusSettings.Text = text;
        TxtRunStatusSettings.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 80, 80))
            : (Brush)FindResource("TextFillColorSecondaryBrush");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtAppVersion.Text = GetAppVersionText();
        Log("HP 性能控制 " + GetAppVersionText() + " 启动（BIOS 直连模式）");

        CheckAutoStartStatus();
        _ = RefreshEnergySaverUiAsync();
        LogHpBridgeStatus();
        StartWatchdog();

        try
        {
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        }
        catch (Exception ex)
        {
            // 个别环境（无消息泵 / 策略限制）下 SystemEvents 不可用。
            // 不影响主功能：看门狗仍能在通道僵死时自愈。
            Log("注册电源事件失败（不影响使用）: " + ex.Message);
        }
    }

    /// <summary>
    /// 启动时报告 HP 状态存储的接入情况，便于确认「防抢模式」是否生效。
    ///
    /// 正常应看到 triggerSource = UserAction(0)。若读到 Other(-1)，
    /// 说明 HP 还处在「看到平衡电源计划就把模式抢回智能模式」的状态 ——
    /// 那正是「切到最冷/平衡/安静/静音会跳回智能模式」的原因。
    /// </summary>
    private async void LogHpBridgeStatus()
    {
        // 包目录扫描、RegLoadAppKey 与 hive 读取全部移出 UI 线程。
        var probe = await Task.Run(() =>
        {
            if (!HpSystemControlBridge.IsAvailable)
            {
                return null;
            }

            return new
            {
                Path = HpSystemControlBridge.HivePath,
                Balanced = HpSystemControlBridge.ReadStoredBalancedMode(),
                Trigger = HpSystemControlBridge.ReadTriggerSource()
            };
        });

        if (probe == null)
        {
            Log("未找到 HP 状态存储（本机可能没装 HP 组件），跳过 HP 侧同步");
            return;
        }

        string path = probe.Path;
        Log("HP 状态存储: " + path);
        Log("  HP 记录的平衡系列 = "
            + (probe.Balanced < 0 ? "读取失败" : GetModeName(probe.Balanced))
            + "，triggerSource = "
            + (probe.Trigger == int.MinValue
                ? "读取失败"
                : probe.Trigger + (probe.Trigger == 0 ? "（UserAction，正常）" : "（Other，会被 HP 抢模式）")));
    }

    /// <summary>
    /// 看门狗：长时间休眠唤醒后，WMI 句柄可能已经失效，或者电源事件压根没送到。
    /// 此时轮询会一直失败却没人发起重连。这里周期性检查「上次成功刷新」距今多久，
    /// 超过阈值就主动重连 —— 保证唤醒后能自动恢复流畅工作，而不是一直卡着。
    /// </summary>
    private void StartWatchdog()
    {
        _watchdogTimer?.Stop();
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WATCHDOG_INTERVAL_MS) };
        _watchdogTimer.Tick += (s, args) =>
        {
            if (_isActuallyExiting) return;
            if (!_bios.IsConnected) return;                       // 未连接由连接路径负责
            if (Volatile.Read(ref _connectBusy) != 0) return;     // 正在重连，别插队

            double idle = (DateTime.UtcNow - _lastSuccessUtc).TotalSeconds;
            if (idle > WATCHDOG_STALE_SECONDS)
            {
                _lastSuccessUtc = DateTime.UtcNow;                // 先复位，避免连续触发
                Log("状态已 " + (int)idle + " 秒未成功刷新，触发自动重连");
                _ = ConnectAsync("看门狗");
            }
        };
        _watchdogTimer.Start();
    }

    /// <summary>
    /// ★ SystemEvents 是在它自己的专用线程上回调的，**不是 UI 线程**。
    /// 原实现直接在这个回调里碰 DispatcherTimer 和控件，会抛
    /// InvalidOperationException（跨线程访问），导致唤醒后重连半途中断 ——
    /// 表现就是「睡一觉回来程序就不干活了」。这里统一切回 UI 线程再处理。
    /// </summary>
    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => HandlePowerModeChanged(e.Mode)), DispatcherPriority.Normal);
    }

    private async void HandlePowerModeChanged(PowerModes mode)
    {
        try
        {
            switch (mode)
            {
                case PowerModes.Resume:
                    Log("系统从睡眠/休眠唤醒，正在恢复 BIOS 通道...");
                    StopPolling();
                    _lastSuccessUtc = DateTime.UtcNow;
                    await ResumeWithRetry();
                    break;

                case PowerModes.Suspend:
                    Log("系统进入睡眠/休眠，暂停轮询...");
                    StopPolling();
                    // Disconnect 会等待 BiosWmiClient 的锁；放后台线程，绝不阻塞 UI 消息泵。
                    await Task.Run(() =>
                    {
                        try { _bios.Disconnect(); } catch { }
                    });
                    break;

                case PowerModes.StatusChange:
                    // 交流/电池切换：刷新一次界面即可（供电不同、可用模式可能不同）。
                    _ = RefreshEnergySaverUiAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("处理电源事件异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 唤醒后重连。ACPI 与电源服务刚恢复时 BIOS 接口往往还没准备好，
    /// 固定等 3 秒并不保险 —— 改成带退避的多次尝试（累计约 13 秒）。
    /// </summary>
    private async Task ResumeWithRetry()
    {
        int[] delays = { 800, 1500, 2500, 3500, 5000 };

        foreach (int delay in delays)
        {
            await Task.Delay(delay);
            if (_isActuallyExiting) return;

            if (await ConnectAsync("唤醒恢复"))
            {
                Log("唤醒后已恢复 BIOS 通道");
                return;
            }
        }

        Log("唤醒后多次重连均未成功，交给看门狗继续尝试");
    }

    /// <summary>读自身程序集版本，形如 "v2.0.0"。</summary>
    private static string GetAppVersionText()
    {
        try
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "v2.0.0";
            return "v" + v.Major + "." + v.Minor + "." + v.Build;
        }
        catch
        {
            return "v2.0.0";
        }
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (!_isActuallyExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        try { SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged; } catch { }
        StopPolling();
        _watchdogTimer?.Stop();
        _watchdogTimer = null;
        // 退出路径也不在 UI 线程等待 WMI 锁；进程结束时系统会回收句柄。
        _ = Task.Run(() =>
        {
            try { _bios.Dispose(); } catch { }
        });
        Log("程序已退出");
    }

    private void MainNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            PerformancePage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: var tag })
        {
            string tagStr = tag?.ToString();
            PerformancePage.Visibility = tagStr == "performance" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>首次连接（App 启动时调用）。</summary>
    public Task<bool> TryAutoConnect() => ConnectAsync("启动");

    /// <summary>
    /// 打开 BIOS WMI 通道。这是全部功能的唯一前置条件 —— 不需要 HP 后台进程，
    /// 也不需要注入任何 DLL。
    ///
    /// 启动、唤醒恢复、轮询失败、看门狗四条路径都走这里，用 _connectBusy 做重入保护：
    /// 唤醒的瞬间「电源事件 + 轮询超时 + 看门狗」可能同时想重连，
    /// 只放一个进来，其余的立刻返回，避免几个 WMI 连接互相踩。
    /// </summary>
    private async Task<bool> ConnectAsync(string reason)
    {
        if (Interlocked.CompareExchange(ref _connectBusy, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            StopPolling();
            // Disconnect 可能要等待正在执行的 ACPI/WMI 调用；不能在 UI 线程同步执行。
            await Task.Run(() =>
            {
                try { _bios.Disconnect(); } catch { }
            });

            UpdateConnStatus("正在连接 BIOS...");
            UpdateRunStatus("正在打开 BIOS 接口...");

            bool ok = await Task.Run(() => _bios.Connect());
            if (!ok)
            {
                Log("[" + reason + "] BIOS 通道连接失败: " + (_bios.GetLastError() ?? "未知错误"));
                UpdateConnStatus("连接失败", true);
                UpdateRunStatus("无法打开 BIOS 接口，请确认以管理员身份运行", true);
                BtnManualRefresh.Visibility = Visibility.Visible;
                return false;
            }

            UpdateConnStatus("已连接");
            // 不再把整个 WPF 进程设为 IDLE/CPU 节流。
            // 该设置会连 UI 线程一起降到低优先级，在 ACPI/WMI 忙时表现为长时间「未响应」。
            BtnManualRefresh.Visibility = Visibility.Collapsed;

            if (!await RefreshAll())
            {
                Log("[" + reason + "] 已打开 BIOS 接口但读取失败");
                UpdateConnStatus("读取失败", true);
                UpdateRunStatus("已打开 BIOS 接口但读取失败", true);
                BtnManualRefresh.Visibility = Visibility.Visible;
                return false;
            }

            CreateModeButtons();
            await SetupFanSectionAsync();
            UpdateTrayModes();
            await RefreshEnergySaverUiAsync();
            StartPolling();

            UpdateRunStatus("运行中");
            Log("[" + reason + "] 系统就绪，轮询已启动");
            return true;
        }
        catch (Exception ex)
        {
            Log("[" + reason + "] 连接过程异常: " + ex.Message);
            UpdateConnStatus("连接异常", true);
            UpdateRunStatus("连接过程异常", true);
            return false;
        }
        finally
        {
            Volatile.Write(ref _connectBusy, 0);
        }
    }

    private void StartPolling()
    {
        StopPolling();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(POLL_INTERVAL_MS) };
        _pollTimer.Tick += (s, args) =>
        {
            // ★ 防叠加：一轮状态刷新最坏要几秒（BIOS 重试 + 风扇读取），
            //   如果比轮询间隔还慢，定时器会不停排队新的 Tick，
            //   于是越积越多、越跑越卡。用 _pollBusy 保证同一时刻只有一轮在跑。
            if (Interlocked.CompareExchange(ref _pollBusy, 1, 0) != 0) return;
            _ = PollStatus();
        };
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        Volatile.Write(ref _pollBusy, 0);
    }

    /// <summary>刷新界面状态。返回 false 表示 BIOS 读取失败，需要重连。</summary>
    private async Task<bool> RefreshAll()
    {
        try
        {
            string status = null;
            bool maxFan = false;
            int[] levels = null;

            await Task.Run(() =>
            {
                status = _bios.GetStatus();
                if (_fanSupported)
                {
                    // 失败不抛出，保留上一轮显示值
                    _bios.GetMaxFan(out maxFan);
                    levels = _bios.GetFanLevels();
                }
            });

            if (status == null)
            {
                Log("读取 BIOS 状态失败: " + (_bios.GetLastError() ?? "未知错误"));
                return false;
            }

            int mode = -1;
            bool energySaverOn = false;
            bool energySaverKnown = false;

            foreach (string part in status.Split('|'))
            {
                int idx = part.IndexOf(':');
                if (idx < 0) continue;

                string key = part.Substring(0, idx);
                string val = part.Substring(idx + 1);
                switch (key)
                {
                    case "MODE":
                        int.TryParse(val, out mode);
                        break;
                    case "POWER":
                        TxtPowerStatus.Text = val;
                        break;
                    case "VERSION":
                        TxtVersionSettings.Text = val;
                        break;
                    case "ESAVER":
                        energySaverKnown = val == "开" || val == "关";
                        energySaverOn = val == "开";
                        break;
                }
            }

            if (mode < 0)
            {
                Log("读取当前模式失败: " + (_bios.GetLastError() ?? "未知错误"));
                return false;
            }

            _currentMode = mode;
            TxtCurrentMode.Text = GetModeName(mode);

            if (_fanSupported)
            {
                UpdateFanUi(maxFan, levels);
            }

            if (energySaverKnown)
            {
                ApplyEnergySaverUi(energySaverOn);
                // 轮询读到的最新节能模式也要同步给托盘菜单，
                // 否则托盘「系统节能模式」勾选状态不会跟随外部改动自动刷新。
                UpdateTrayEnergySaver();
            }

            UpdateModeButtons();
            UpdateTrayCurrentMode();
            _lastSuccessUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Log("刷新状态异常: " + ex.Message);
            return false;
        }
    }

    private async Task PollStatus()
    {
        try
        {
            if (!_bios.IsConnected)
            {
                Log("BIOS 通道已断开，尝试重新连接...");
                UpdateConnStatus("连接断开", true);
                UpdateRunStatus("正在重新连接...", true);
                await ConnectAsync("断线重连");
                return;
            }

            if (!await RefreshAll())
            {
                Log("读取失败，尝试重新连接...");
                UpdateConnStatus("连接异常", true);
                UpdateRunStatus("正在重新连接...", true);
                await ConnectAsync("读取失败重连");
            }
        }
        catch (Exception ex)
        {
            Log("轮询异常: " + ex.Message);
            await ConnectAsync("轮询异常重连");
        }
        finally
        {
            // 无论成败都要放锁，否则轮询会永久停摆
            Volatile.Write(ref _pollBusy, 0);
        }
    }

    private void CreateModeButtons()
    {
        ModePanel.Children.Clear();
        _modeButtons.Clear();

        List<int> modes = _bios.GetSupportedModes();
        if (modes.Count == 0)
        {
            Log("未获取到支持的模式列表");
            return;
        }

        Log("支持 " + modes.Count + " 个模式");
        foreach (int mode in modes)
        {
            ToggleButton btn = new ToggleButton
            {
                Tag = mode,
                Width = 148,
                Height = 88,
                Margin = new Thickness(4, 4, 4, 4),
                IsChecked = mode == _currentMode
            };

            StackPanel panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (ModeIcons.TryGetValue(mode, out var icon))
            {
                panel.Children.Add(new FontIcon
                {
                    Glyph = icon,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                    FontFamily = new FontFamily("Segoe Fluent Icons")
                });
            }

            TextBlock nameBlock = new TextBlock
            {
                Text = GetModeName(mode),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(nameBlock);

            if (ModeDescriptions.TryGetValue(mode, out var desc))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 124,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            btn.Content = panel;
            btn.Click += ModeButton_Click;
            _modeButtons[mode] = btn;
            ModePanel.Children.Add(btn);
        }
    }

    private async void BtnManualRefresh_Click(object sender, RoutedEventArgs e)
    {
        BtnManualRefresh.Visibility = Visibility.Collapsed;
        await ConnectAsync("手动刷新");
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: int mode }) return;
        if (mode == _currentMode) return;

        SetModeButtonsEnabled(false);
        Log("设置模式: " + GetModeName(mode));

        bool ok = await Task.Run(() => _bios.SetMode(mode));
        Log(ok ? "设置成功" : "设置失败: " + (_bios.GetLastError() ?? "未知错误"));

        if (ok)
        {
            _currentMode = mode;
            await RefreshAll();
            UpdateTrayCurrentMode();
        }
        else
        {
            // 失败时把按钮状态拨回真实值，避免界面显示的和实际的不一致
            UpdateModeButtons();
        }

        SetModeButtonsEnabled(true);
    }

    private void SetModeButtonsEnabled(bool enabled)
    {
        foreach (var kv in _modeButtons)
        {
            kv.Value.IsEnabled = enabled;
        }
    }

    private void UpdateModeButtons()
    {
        foreach (var kv in _modeButtons)
        {
            kv.Value.IsChecked = kv.Key == _currentMode;
        }
    }

    // ================================================================
    //  系统节能模式（独立开关）
    //
    //  走 WNF 广播（见 EnergySaverManager），普通用户态即可，不需要提权。
    //  与热控模式相互独立：热控模式切换时会「顺带」同步一次节能模式
    //  （省电模式→开、其它→关），之后用户手动改回来不会被覆盖。
    // ================================================================

    /// <summary>读一次真实状态并刷新开关显示（不会触发写回）。</summary>
    private async Task RefreshEnergySaverUiAsync()
    {
        // 注册表读取也不放在 Dispatcher 上，避免电源服务瞬时繁忙时拖住界面。
        int state = await Task.Run(() => EnergySaverManager.GetState());
        if (state < 0 || _isActuallyExiting)
        {
            return;
        }

        ApplyEnergySaverUi(state == EnergySaverManager.STATE_ON);
        UpdateTrayEnergySaver();
    }

    /// <summary>只更新界面，不写系统。_isUpdatingEnergySaver 阻断 Toggled 回环。</summary>
    private void ApplyEnergySaverUi(bool on)
    {
        _isUpdatingEnergySaver = true;
        ToggleEnergySaver.IsOn = on;
        _isUpdatingEnergySaver = false;
    }

    private async void ToggleEnergySaver_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingEnergySaver) return;

        bool want = ToggleEnergySaver.IsOn;
        ToggleEnergySaver.IsEnabled = false;
        Log("设置系统节能模式: " + (want ? "开" : "关"));

        string error = null;
        bool ok = await Task.Run(() =>
        {
            string inner;
            bool r = _bios.SetEnergySaver(want, out inner);
            error = inner;
            return r;
        });

        if (ok)
        {
            Log("系统节能模式已" + (want ? "开启" : "关闭"));

            // 电源服务是异步落盘的，稍后读一次真实值回来校正界面
            await Task.Delay(600);
            await RefreshEnergySaverUiAsync();
        }
        else
        {
            Log("设置系统节能模式失败: " + (error ?? "未知错误"));
            ApplyEnergySaverUi(!want);
        }

        ToggleEnergySaver.IsEnabled = true;
        UpdateTrayEnergySaver();
    }

    // ================================================================
    //  风扇
    //
    //  能读到的只是 BIOS 的「转速级别」与「最大转速」开关。
    //  实际 RPM / 百分比走 EC，需要内核驱动，本程序不碰。
    //  本机 BIOS 不暴露风扇指令时整块隐藏 —— 不给用户一个点了没反应的控件。
    // ================================================================

    private async Task SetupFanSectionAsync()
    {
        _fanSupported = _bios.IsFanSupported;

        if (!_fanSupported)
        {
            FanHeaderText.Visibility = Visibility.Collapsed;
            FanSection.Visibility = Visibility.Collapsed;
            return;
        }

        _isUpdatingFan = true;
        SliderFanLevel.Maximum = BiosWmiClient.FAN_LEVEL_MAX;
        SliderFanLevel.Value = 0;
        _isUpdatingFan = false;

        FanHeaderText.Visibility = Visibility.Visible;
        FanSection.Visibility = Visibility.Visible;
        UpdateFanLevelLabel();

        // GetFanTypes 会访问 ACPI/WMI，不能在 UI 线程执行。
        string[] names = await Task.Run(BuildFanTypeNames);
        if (!_isActuallyExiting)
        {
            Log("风扇控制已启用：" + _bios.FanCount + " 个风扇（" + string.Join("、", names) + "）");
        }
    }

    /// <summary>按 BIOS 返回的风扇类型给出显示名，读不到就退回「风扇 N」。</summary>
    private string[] BuildFanTypeNames()
    {
        int[] types = _bios.GetFanTypes();
        int count = Math.Max(1, _bios.FanCount);
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = (types != null && i < types.Length && types[i] > 0)
                ? BiosWmiClient.FanTypeName(types[i])
                : ("风扇 " + (i + 1));
        }
        return names;
    }

    /// <summary>轮询回调：只更新界面，不写 BIOS。滑块位置不跟随，避免打断用户拖动。</summary>
    private void UpdateFanUi(bool maxFan, int[] levels)
    {
        _isUpdatingFan = true;
        ToggleMaxFan.IsOn = maxFan;
        _isUpdatingFan = false;

        if (levels == null || levels.Length == 0)
        {
            TxtFanDetail.Text = "当前转速级别读取失败";
            return;
        }

        var parts = new List<string>();
        for (int i = 0; i < levels.Length; i++)
        {
            parts.Add("风扇 " + (i + 1) + " = " + levels[i]);
        }
        TxtFanDetail.Text = "当前 BIOS 转速级别：" + string.Join(" · ", parts);
    }

    private void UpdateFanLevelLabel()
    {
        if (TxtFanLevelValue == null) return;
        TxtFanLevelValue.Text = (int)Math.Round(SliderFanLevel.Value) + " / " + (int)SliderFanLevel.Maximum;
    }

    private void SliderFanLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingFan) return;
        UpdateFanLevelLabel();
    }

    private async void ToggleMaxFan_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFan || !_fanSupported) return;

        bool want = ToggleMaxFan.IsOn;
        Log("设置最大转速: " + (want ? "开" : "关"));

        bool ok = await Task.Run(() => _bios.SetMaxFan(want));
        if (ok)
        {
            Log("最大转速已" + (want ? "开启" : "关闭"));
            return;
        }

        Log("设置最大转速失败: " + (_bios.GetLastError() ?? "未知错误"));
        _isUpdatingFan = true;
        ToggleMaxFan.IsOn = !want;
        _isUpdatingFan = false;
    }

    private async void BtnApplyFanLevel_Click(object sender, RoutedEventArgs e)
    {
        if (!_fanSupported) return;

        int level = (int)Math.Round(SliderFanLevel.Value);
        int count = Math.Max(1, _bios.FanCount);
        var payload = new int[count];
        for (int i = 0; i < count; i++)
        {
            payload[i] = level;
        }

        BtnApplyFanLevel.IsEnabled = false;
        Log("设置风扇转速级别: " + level + "（" + count + " 个风扇）");

        bool ok = await Task.Run(() => _bios.SetFanLevels(payload));

        if (ok)
        {
            Log("转速级别已写入 BIOS（返回码 0）");
            await RefreshAll();
        }
        else
        {
            Log("设置转速级别失败: " + (_bios.GetLastError() ?? "未知错误"));
        }

        BtnApplyFanLevel.IsEnabled = true;
    }

    private async void CheckAutoStartStatus()
    {
        try
        {
            bool taskExists = await Task.Run(() =>
            {
                try
                {
                    using Process p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = "/query /tn \"" + TASK_NAME + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    p.WaitForExit(5000);
                    return p.ExitCode == 0;
                }
                catch
                {
                    return false;
                }
            });

            _isUpdatingAutoStart = true;
            ToggleAutoStart.IsOn = taskExists;
            _isUpdatingAutoStart = false;
            UpdateTrayAutoStart();
        }
        catch
        {
            _isUpdatingAutoStart = true;
            ToggleAutoStart.IsOn = false;
            _isUpdatingAutoStart = false;
        }
    }

    private async void ToggleAutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (!base.IsLoaded || _isUpdatingAutoStart) return;

        if (ToggleAutoStart.IsOn)
        {
            if (!await CreateScheduledTask())
            {
                Log("创建开机启动任务失败");
                _isUpdatingAutoStart = true;
                ToggleAutoStart.IsOn = false;
                _isUpdatingAutoStart = false;
            }
            else
            {
                Log("已启用开机自动启动");
                UpdateTrayAutoStart();
            }
        }
        else
        {
            if (!await DeleteScheduledTask())
            {
                Log("删除开机启动任务失败");
                _isUpdatingAutoStart = true;
                ToggleAutoStart.IsOn = true;
                _isUpdatingAutoStart = false;
            }
            else
            {
                Log("已禁用开机自动启动");
                UpdateTrayAutoStart();
            }
        }
    }

    private Task<bool> CreateScheduledTask()
    {
        return Task.Run(() =>
        {
            try
            {
                string fileName = Process.GetCurrentProcess().MainModule.FileName;
                string arguments = "/c schtasks.exe /create /tn \"" + TASK_NAME + "\" /tr \"\\\"" + fileName + "\\\" --hide\" /sc onlogon /rl highest /f";
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Verb = "runas"
                };

                using Process p = Process.Start(startInfo);
                p.WaitForExit(30000);
                if (p.ExitCode == 0)
                {
                    base.Dispatcher.Invoke(() => Log("任务计划已创建: " + TASK_NAME));
                    return true;
                }
                base.Dispatcher.Invoke(() => Log("schtasks 返回退出码: " + p.ExitCode));
                return false;
            }
            catch (Exception ex)
            {
                base.Dispatcher.Invoke(() => Log("创建计划任务异常: " + ex.Message));
                return false;
            }
        });
    }

    public Task<bool> DeleteScheduledTask()
    {
        return Task.Run(() =>
        {
            try
            {
                string arguments = "/c schtasks.exe /delete /tn \"" + TASK_NAME + "\" /f";
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Verb = "runas"
                };

                using Process p = Process.Start(startInfo);
                p.WaitForExit(30000);
                if (p.ExitCode == 0)
                {
                    base.Dispatcher.Invoke(() => Log("任务计划已删除: " + TASK_NAME));
                    return true;
                }
                base.Dispatcher.Invoke(() => Log("schtasks 删除返回退出码: " + p.ExitCode));
                return false;
            }
            catch (Exception ex)
            {
                base.Dispatcher.Invoke(() => Log("删除计划任务异常: " + ex.Message));
                return false;
            }
        });
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (base.WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void UpdateTrayModes()
    {
        ((App)Application.Current).UpdateModeMenuItems(_bios.GetSupportedModes(), _currentMode);
    }

    private void UpdateTrayCurrentMode()
    {
        ((App)Application.Current).UpdateCurrentMode(_currentMode);
    }

    private void UpdateTrayAutoStart()
    {
        ((App)Application.Current).UpdateAutoStartMenuState(ToggleAutoStart.IsOn);
    }

    private void UpdateTrayEnergySaver()
    {
        ((App)Application.Current).UpdateEnergySaverMenuState(ToggleEnergySaver.IsOn);
    }

    /// <summary>
    /// 托盘菜单选了模式。★ 整段回到 UI 线程执行，且 WMI 写入放后台：
    /// 原实现直接在托盘线程上同步调 SetMode，会阻塞托盘线程几百毫秒
    /// （菜单「粘住」不消失、界面也点不动）。
    /// </summary>
    public async void SetModeFromTray(int mode)
    {
        if (!_bios.IsConnected) return;

        if (mode == _currentMode)
        {
            UpdateTrayCurrentMode();
            return;
        }

        Log("【托盘】设置模式: " + GetModeName(mode));

        bool ok = await Task.Run(() => _bios.SetMode(mode));
        if (ok)
        {
            _currentMode = mode;
            await RefreshAll();
            UpdateTrayCurrentMode();
        }
        else
        {
            Log("【托盘】设置模式失败: " + (_bios.GetLastError() ?? "未知错误"));
            UpdateTrayCurrentMode();
        }
    }

    /// <summary>托盘菜单切了「系统节能模式」。</summary>
    public async void SetEnergySaverFromTray(bool on)
    {
        int current = await Task.Run(() => EnergySaverManager.GetState());
        if (on == (current == EnergySaverManager.STATE_ON))
        {
            await RefreshEnergySaverUiAsync();
            return;
        }

        Log("【托盘】设置系统节能模式: " + (on ? "开" : "关"));

        string error = null;
        bool ok = await Task.Run(() =>
        {
            string inner;
            bool r = _bios.SetEnergySaver(on, out inner);
            error = inner;
            return r;
        });

        if (ok)
        {
            Log("【托盘】系统节能模式已" + (on ? "开启" : "关闭"));
            await Task.Delay(600);
            await RefreshEnergySaverUiAsync();
        }
        else
        {
            Log("【托盘】设置系统节能模式失败: " + (error ?? "未知错误"));
            await RefreshEnergySaverUiAsync();
        }

        UpdateTrayEnergySaver();
    }

    public async void ToggleAutoStartFromTray(bool enable)
    {
        if (!base.IsLoaded) return;

        if (enable)
        {
            if (await CreateScheduledTask())
            {
                Log("【托盘】已启用开机自动启动");
                _isUpdatingAutoStart = true;
                ToggleAutoStart.IsOn = true;
                _isUpdatingAutoStart = false;
            }
            else
            {
                Log("【托盘】启用开机自启失败");
                UpdateTrayAutoStart();
            }
        }
        else
        {
            if (await DeleteScheduledTask())
            {
                Log("【托盘】已禁用开机自动启动");
                _isUpdatingAutoStart = true;
                ToggleAutoStart.IsOn = false;
                _isUpdatingAutoStart = false;
            }
            else
            {
                Log("【托盘】禁用开机自启失败");
                UpdateTrayAutoStart();
            }
        }
    }

    public void PrepareForExit()
    {
        _isActuallyExiting = true;
        StopPolling();
        _watchdogTimer?.Stop();
        _watchdogTimer = null;
        try { SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged; } catch { }
        // 退出路径不在 UI 线程等待 WMI 锁；进程结束时系统会回收句柄。
        _ = Task.Run(() =>
        {
            try { _bios.Dispose(); } catch { }
        });
        Log("程序已退出");
    }
}
