using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls;

namespace QuickHPControl;

public partial class MainWindow : Window, IComponentConnector
{
	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	private readonly IpcClient _ipc = new IpcClient();
	private readonly Dictionary<int, ToggleButton> _modeButtons = new Dictionary<int, ToggleButton>();
	private Process _targetProcess;
	private DispatcherTimer _pollTimer;
	private int _currentMode = -1;
	private bool _isUpdatingAutoStart;
	private bool _isActuallyExiting;

	private const string BOOTSTRAP_DLL = "BootstrapNative.dll";
	private const string TASK_NAME = "QuickHPControlAutoStart";
	private const string HPX_PROCESS_NAME = "HP.HPX";
	private const int SW_HIDE = 0;

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

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetParent(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
		_ipc.LogCallback = Log;
		base.Loaded += MainWindow_Loaded;
		base.Closing += MainWindow_Closing;
		base.StateChanged += MainWindow_StateChanged;
	}

	private void Log(string message)
	{
		string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
		base.Dispatcher.Invoke(() =>
		{
			TxtLog.AppendText(line + "\n");
			TxtLog.ScrollToEnd();
		});
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
		Log("HP 性能控制启动");
		CheckAutoStartStatus();
	}

	private void MainWindow_Closing(object sender, CancelEventArgs e)
	{
		if (!_isActuallyExiting)
		{
			e.Cancel = true;
			Hide();
			return;
		}

		_pollTimer?.Stop();
		try { _ipc.Dispose(); } catch { }
		if (_targetProcess != null)
		{
			try { _targetProcess.Dispose(); } catch { }
			_targetProcess = null;
		}
		Log("程序已退出（DLL 保持驻留）");
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

	public async Task TryAutoConnect()
	{
		_targetProcess = Injector.FindTargetProcess();
		if (_targetProcess == null && App.IsHideOnStartup())
		{
			for (int retry = 0; retry < 60; retry++)
			{
				if (retry == 0)
					Log("未找到 HP.SystemControl.Background.exe 进程，等待就绪...");
				else
					Log($"未找到目标进程，第 {retry} 次重试...");

				UpdateConnStatus($"进程未运行 ({retry + 1}/12)");
				await Task.Delay(3000);
				_targetProcess = Injector.FindTargetProcess();
				if (_targetProcess != null) break;
			}
		}

		if (_targetProcess == null)
		{
			Log("未找到进程，尝试通过 URI 协议启动 HP 后台进程...");
			UpdateRunStatus("正在启动 HP 后台进程...");
			KillProcessesByName("HP.HPX");

			try
			{
				Process.Start(new ProcessStartInfo("hpx://pcsystemcontrol") { UseShellExecute = true });
			}
			catch (Exception ex)
			{
				Log("通过 URI 协议启动失败: " + ex.Message);
				UpdateConnStatus("进程未运行", true);
				UpdateRunStatus("HP 后台进程启动失败，请确认 myHP 已安装", true);
				base.Dispatcher.Invoke(() => BtnManualRefresh.Visibility = Visibility.Visible);
				return;
			}

			Log("已通过 URI 协议发送启动请求");
			UpdateRunStatus("等待进程就绪...");

			using var hpxHideCts = new CancellationTokenSource();
			var hpxHideTask = Task.Run(async () =>
			{
				while (!hpxHideCts.Token.IsCancellationRequested)
				{
					HideWindowsByProcessName("HP.HPX");
					await Task.Delay(100, hpxHideCts.Token);
				}
			}, hpxHideCts.Token);

			_targetProcess = null;
			for (int retry = 0; retry < 12; retry++)
			{
				await Task.Delay(5000);
				_targetProcess = Injector.FindTargetProcess();
				if (_targetProcess != null) break;
				Log($"第 {retry + 1} 次查找未找到进程，继续等待...");
				UpdateRunStatus($"等待进程就绪... ({(retry + 1) * 5}/60s)");
			}

			hpxHideCts.Cancel();
			try { await hpxHideTask; } catch { }

			if (_targetProcess == null)
			{
				Log("启动后 60s 内无法找到进程");
				UpdateConnStatus("进程未运行", true);
				UpdateRunStatus("HP 后台进程启动后无法找到", true);
				base.Dispatcher.Invoke(() => BtnManualRefresh.Visibility = Visibility.Visible);
				return;
			}
		}

		Log("找到目标进程 PID=" + _targetProcess.Id);
		UpdateConnStatus("已找到进程");
		KillProcessesByName("HP.HPX");

		if (!InjectPayload())
		{
			UpdateConnStatus("注入失败", true);
			UpdateRunStatus("DLL 注入失败，请检查管理员权限和防病毒设置", true);
			return;
		}

		Log("等待 HOOK 端 TCP 服务就绪...");
		bool ready = false;
		for (int retry = 0; retry < 30; retry++)
		{
			await Task.Delay(500);
			try
			{
				using var testClient = new TcpClient();
				IAsyncResult ar = testClient.BeginConnect("127.0.0.1", 26745, null, null);
				if (ar.AsyncWaitHandle.WaitOne(300))
				{
					testClient.EndConnect(ar);
					ready = true;
					break;
				}
			}
			catch { }
		}

		if (!ready)
		{
			Log("TCP 服务未就绪（超时），触发重新连接...");
			UpdateConnStatus("服务未响应", true);
			UpdateRunStatus("HOOK 端 TCP 服务启动超时，正在重新连接...", true);
			await ReconnectAndInject();
			return;
		}

		Log("TCP 服务已就绪");
		if (!_ipc.Connect())
		{
			UpdateConnStatus("连接失败", true);
			UpdateRunStatus("IPC 连接失败", true);
			return;
		}

		UpdateConnStatus("已连接");
		await RefreshAll();
		CreateModeButtons();
		UpdateTrayModes();

		_pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		_pollTimer.Tick += async (s, args) => await PollStatus();
		_pollTimer.Start();

		UpdateRunStatus("运行中");
		Log("系统就绪，轮询已启动");
	}

	private bool InjectPayload()
	{
		string bootstrapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BootstrapNative.dll");
		if (!File.Exists(bootstrapPath))
		{
			Log("未找到 BootstrapNative.dll: " + bootstrapPath);
			Log("请先构建 BootstrapNative 项目");
			return false;
		}

		Log("注入 BootstrapNative.dll ...");
		bool result = Injector.Inject(_targetProcess, bootstrapPath, Log);
		if (result)
		{
			Log("注入线程已启动");
		}
		else
		{
			Log("注入失败");
		}
		return result;
	}

	private async Task RefreshAll()
	{
		try
		{
			int mode = _ipc.GetCurrentMode();
			if (mode >= 0)
			{
				_currentMode = mode;
				TxtCurrentMode.Text = GetModeName(mode);
			}

			string nameResp = _ipc.SendCommand("GET_MODE_NAME");
			if (nameResp != null && nameResp.StartsWith("NAME:"))
			{
				TxtCurrentMode.Text = nameResp.Substring(5);
			}

			int ver = _ipc.GetVersion();
			TxtVersionSettings.Text = "v" + ver;

			string status = _ipc.GetStatus();
			if (status != null)
			{
				foreach (string part in status.Split('|'))
				{
					int idx = part.IndexOf(':');
					if (idx >= 0)
					{
						string key = part.Substring(0, idx);
						string val = part.Substring(idx + 1);
						if (key == "POWER")
						{
							TxtPowerStatus.Text = val;
						}
					}
				}
			}

			UpdateModeButtons();
			UpdateTrayCurrentMode();
		}
		catch (Exception ex)
		{
			Log("刷新状态异常: " + ex.Message);
		}
	}

	private async Task PollStatus()
	{
		if (!_ipc.IsConnected)
		{
			Log("IPC 连接断开，尝试重新连接...");
			UpdateConnStatus("连接断开", true);
			UpdateRunStatus("正在重新连接...", true);
			_pollTimer?.Stop();
			await ReconnectAndInject();
			return;
		}

		try
		{
			await RefreshAll();
		}
		catch (Exception ex)
		{
			Log("轮询异常: " + ex.Message);
			Log("尝试重新连接...");
			_pollTimer?.Stop();
			await ReconnectAndInject();
		}
	}

	private void KillProcessesByName(string processName)
	{
		foreach (Process proc in Process.GetProcessesByName(processName))
		{
			try
			{
				if (!proc.HasExited)
				{
					Log($"结束 {processName}.exe 进程 PID={proc.Id}");
					proc.Kill();
					proc.WaitForExit(3000);
				}
			}
			catch (Exception ex)
			{
				Log("结束 " + processName + ".exe 失败: " + ex.Message);
			}
			try { proc.Dispose(); } catch { }
		}
	}

	private void HideWindowsByProcessName(string processName)
	{
		HashSet<uint> targetPids = new HashSet<uint>(
			Process.GetProcessesByName(processName).Select(p => (uint)p.Id));

		if (targetPids.Count == 0) return;

		EnumWindows((hWnd, _) =>
		{
			if (!IsWindowVisible(hWnd)) return true;

			GetWindowThreadProcessId(hWnd, out var pid);
			if (targetPids.Contains(pid))
			{
				ShowWindowAsync(hWnd, SW_HIDE);
				return true;
			}

			EnumChildWindows(hWnd, (childHWnd, lParam) =>
			{
				GetWindowThreadProcessId(childHWnd, out var childPid);
				if (targetPids.Contains(childPid))
				{
					ShowWindowAsync(hWnd, SW_HIDE);
					return false;
				}
				return true;
			}, IntPtr.Zero);

			return true;
		}, IntPtr.Zero);
	}

	private async Task ReconnectAndInject()
	{
		try { _ipc.Dispose(); } catch { }

		if (_targetProcess != null)
		{
			try
			{
				if (!_targetProcess.HasExited)
				{
					Log("结束 HP.SystemControl.Background.exe 进程 PID=" + _targetProcess.Id);
					_targetProcess.Kill();
					await Task.Delay(1000);
				}
			}
			catch (Exception ex)
			{
				Log("结束进程失败: " + ex.Message);
			}
			try { _targetProcess.Dispose(); } catch { }
			_targetProcess = null;
		}

		Log("重新启动 HP 后台进程...");
		UpdateRunStatus("正在重新启动 HP 后台进程...");
		KillProcessesByName("HP.HPX");

		try
		{
			Process.Start(new ProcessStartInfo("hpx://pcsystemcontrol") { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			Log("通过 URI 协议重新启动失败: " + ex.Message);
			UpdateConnStatus("启动失败", true);
			UpdateRunStatus("HP 后台进程重新启动失败", true);
			base.Dispatcher.Invoke(() => BtnManualRefresh.Visibility = Visibility.Visible);
			return;
		}

		Log("已通过 URI 协议发送重新启动请求");
		UpdateRunStatus("等待进程就绪...");

		using var hpxHideCts = new CancellationTokenSource();
		var hpxHideTask = Task.Run(async () =>
		{
			while (!hpxHideCts.Token.IsCancellationRequested)
			{
				HideWindowsByProcessName("HP.HPX");
				await Task.Delay(100, hpxHideCts.Token);
			}
		}, hpxHideCts.Token);

		_targetProcess = null;
		for (int i = 0; i < 12; i++)
		{
			await Task.Delay(5000);
			_targetProcess = Injector.FindTargetProcess();
			if (_targetProcess != null) break;
			Log($"第 {i + 1} 次查找未找到进程，继续等待...");
			UpdateRunStatus($"等待进程就绪... ({(i + 1) * 5}/60s)");
		}

		hpxHideCts.Cancel();
		try { await hpxHideTask; } catch { }

		if (_targetProcess == null)
		{
			Log("重新启动后 60s 内无法找到进程");
			UpdateConnStatus("进程未运行", true);
			UpdateRunStatus("HP 后台进程重新启动后无法找到", true);
			base.Dispatcher.Invoke(() => BtnManualRefresh.Visibility = Visibility.Visible);
			return;
		}

		Log("找到目标进程 PID=" + _targetProcess.Id);
		UpdateConnStatus("已找到进程");
		KillProcessesByName("HP.HPX");

		if (!InjectPayload())
		{
			UpdateConnStatus("注入失败", true);
			UpdateRunStatus("DLL 注入失败，请检查管理员权限和防病毒设置", true);
			return;
		}

		Log("等待 HOOK 端 TCP 服务就绪...");
		bool ready = false;
		for (int i = 0; i < 30; i++)
		{
			await Task.Delay(500);
			try
			{
				using var testClient = new TcpClient();
				IAsyncResult ar = testClient.BeginConnect("127.0.0.1", 26745, null, null);
				if (ar.AsyncWaitHandle.WaitOne(300))
				{
					testClient.EndConnect(ar);
					ready = true;
					break;
				}
			}
			catch { }
		}

		if (!ready)
		{
			Log("TCP 服务未就绪（超时），重新连接失败");
			UpdateConnStatus("服务未响应", true);
			UpdateRunStatus("HOOK 端 TCP 服务启动超时，重连失败", true);
			base.Dispatcher.Invoke(() => BtnManualRefresh.Visibility = Visibility.Visible);
			return;
		}

		Log("TCP 服务已就绪");
		if (!_ipc.Connect())
		{
			UpdateConnStatus("连接失败", true);
			UpdateRunStatus("IPC 连接失败", true);
			return;
		}

		UpdateConnStatus("已连接");
		await RefreshAll();
		CreateModeButtons();
		UpdateTrayModes();

		_pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
		_pollTimer.Tick += async (s, args) => await PollStatus();
		_pollTimer.Start();

		UpdateRunStatus("运行中");
		Log("重新连接成功，轮询已重启");
	}

	private void CreateModeButtons()
	{
		ModePanel.Children.Clear();
		_modeButtons.Clear();

		List<int> modes = _ipc.GetSupportedModes();
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
				Margin = new Thickness(0, 0, 8, 8),
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
			btn.Checked += ModeButton_Checked;
			btn.Unchecked += ModeButton_Unchecked;
			_modeButtons[mode] = btn;
			ModePanel.Children.Add(btn);
		}
	}

	private void ModeButton_Checked(object sender, RoutedEventArgs e) { }
	private void ModeButton_Unchecked(object sender, RoutedEventArgs e) { }

	private async void BtnManualRefresh_Click(object sender, RoutedEventArgs e)
	{
		BtnManualRefresh.Visibility = Visibility.Collapsed;
		UpdateConnStatus("手动重试中...");
		UpdateRunStatus("正在查找 HP 后台进程...");
		await TryAutoConnect();
	}

	private async void ModeButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not ToggleButton { Tag: int mode }) return;

		foreach (var kv in _modeButtons)
		{
			kv.Value.IsEnabled = false;
		}

		Log("设置模式: " + GetModeName(mode));
		bool ok = _ipc.SetMode(mode);
		Log(ok ? "设置成功" : "设置失败");

		if (ok)
		{
			_currentMode = mode;
			await RefreshAll();
			UpdateTrayCurrentMode();
		}

		foreach (var kv in _modeButtons)
		{
			kv.Value.IsEnabled = true;
		}
	}

	private void UpdateModeButtons()
	{
		foreach (var kv in _modeButtons)
		{
			kv.Value.IsChecked = kv.Key == _currentMode;
		}
	}

	private void CheckAutoStartStatus()
	{
		try
		{
			using Process p = Process.Start(new ProcessStartInfo
			{
				FileName = "schtasks.exe",
				Arguments = "/query /tn \"QuickHPControlAutoStart\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			p.WaitForExit(5000);
			bool taskExists = p.ExitCode == 0;
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
				string arguments = "/c schtasks.exe /create /tn \"QuickHPControlAutoStart\" /tr \"\\\"" + fileName + "\\\" --hide\" /sc onlogon /rl highest /f";
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
					base.Dispatcher.Invoke(() => Log("任务计划已创建: QuickHPControlAutoStart"));
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
				string arguments = "/c schtasks.exe /delete /tn \"QuickHPControlAutoStart\" /f";
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
					base.Dispatcher.Invoke(() => Log("任务计划已删除: QuickHPControlAutoStart"));
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
		((App)Application.Current).UpdateModeMenuItems(_ipc.GetSupportedModes(), _currentMode);
	}

	private void UpdateTrayCurrentMode()
	{
		((App)Application.Current).UpdateCurrentMode(_currentMode);
	}

	private void UpdateTrayAutoStart()
	{
		((App)Application.Current).UpdateAutoStartMenuState(ToggleAutoStart.IsOn);
	}

	public void SetModeFromTray(int mode)
	{
		if (!_ipc.IsConnected) return;

		Log("【托盘】设置模式: " + GetModeName(mode));
		if (_ipc.SetMode(mode))
		{
			_currentMode = mode;
			base.Dispatcher.InvokeAsync(async () =>
			{
				await RefreshAll();
				UpdateTrayCurrentMode();
			});
		}
		else
		{
			Log("【托盘】设置模式失败");
			UpdateTrayCurrentMode();
		}
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
		_pollTimer?.Stop();
		try { _ipc.Dispose(); } catch { }
		if (_targetProcess != null)
		{
			try { _targetProcess.Dispose(); } catch { }
			_targetProcess = null;
		}
		Log("程序已退出（DLL 保持驻留）");
	}
}
