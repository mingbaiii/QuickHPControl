using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickHPControl;

public partial class App : Application
{
	private Win32Tray _tray;
	private int _lastTrayMode = -1;
	private int _trayIconGeneration;
	private readonly HashSet<int> _modeTags = new HashSet<int>();

	// 托盘菜单项 tag。>= 0 的一律是热控模式值，负数保留给命令项。
	private const int TAG_ENERGY_SAVER = -5;
	private const int TAG_SHOW_WINDOW = -4;
	private const int TAG_EXIT = -3;
	private const int TAG_AUTO_START = -1;
	private const int TAG_SEPARATOR = -10;
	private const int TAG_PLACEHOLDER = -99;

	private Mutex _singleInstanceMutex;
	private EventWaitHandle _showWindowEvent;
	private bool _autoStartChecked;
	private bool _energySaverChecked;

	public static bool IsHideOnStartup()
	{
		string[] args = Environment.GetCommandLineArgs();
		for (int i = 1; i < args.Length; i++)
		{
			if (args[i] == "--hide")
			{
				return true;
			}
		}
		return false;
	}

	// 说明：旧实现曾在此挂 AssemblyResolve 钩子，为注入的 PayloadDLL 顶替
	// Newtonsoft.Json 依赖。新方案直连 BIOS、不加载 HP 任何程序集，钩子已移除。

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		// 诊断开关：QuickHPControl.exe --esaver-test
		// 做一次节能模式「开→关→还原」往返，结果写到 %TEMP%\QuickHPControl-esaver-test.txt 后退出。
		// 用于验证 WNF 通道在当前 Windows 版本上仍然有效。
		if (e.Args != null && e.Args.Length > 0 && e.Args[0] == "--esaver-test")
		{
			RunEnergySaverSelfTest();
			Shutdown();
			return;
		}

		_singleInstanceMutex = new Mutex(true, "Global\\QuickHPControl_SingleInstance", out var createdNew);
		if (!createdNew)
		{
			_singleInstanceMutex.Dispose();
			_singleInstanceMutex = null;
			SignalExistingInstance();
			Shutdown();
			return;
		}

		base.ShutdownMode = ShutdownMode.OnExplicitShutdown;

		base.DispatcherUnhandledException += (s, args) => args.Handled = true;
		AppDomain.CurrentDomain.UnhandledException += (s, args) => { _ = args.ExceptionObject; };

		base.MainWindow = new MainWindow();

		// 托盘菜单在连接前就要能显示，节能模式状态直接读一次系统真值
		_energySaverChecked = EnergySaverManager.GetState() == EnergySaverManager.STATE_ON;

		CreateTrayIcon();
		StartShowWindowListener();

		if (!IsHideOnStartup())
		{
			base.MainWindow.Show();
		}

		_ = ((MainWindow)base.MainWindow).TryAutoConnect();
	}

	// ================================================================
	//  诊断：节能模式 WNF 通道自检
	//
	//  用法: QuickHPControl.exe --esaver-test
	//  做一次 开 → 关 → 还原 往返，把过程写到
	//    %TEMP%\QuickHPControl-esaver-test.txt
	//  然后退出。用来确认 WNF 状态名在当前 Windows 版本上仍然有效。
	// ================================================================
	private static void RunEnergySaverSelfTest()
	{
		var sb = new System.Text.StringBuilder();
		string path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(), "QuickHPControl-esaver-test.txt");

		try
		{
			sb.AppendLine("QuickHPControl 节能模式 WNF 通道自检");
			sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.AppendLine("WNF 状态名: 0x41C6013DA3BC3075");
			sb.AppendLine();

			int baseline = EnergySaverManager.GetState();
			sb.AppendLine("基线 EnergySaverState = " + baseline
						  + " (" + EnergySaverManager.Describe(baseline) + ")");

			if (baseline < 0)
			{
				sb.AppendLine("读取失败，终止。");
			}
			else
			{
				bool toOn = baseline != EnergySaverManager.STATE_ON;

				sb.AppendLine();
				sb.AppendLine(">>> 切到 " + (toOn ? "开" : "关"));
				string err1;
				bool ok1 = EnergySaverManager.SetEnabled(toOn, out err1);
				sb.AppendLine("    SetEnabled -> " + (ok1 ? "成功" : "失败")
							  + (err1 != null ? "  (" + err1 + ")" : ""));
				sb.AppendLine("    读回 = " + EnergySaverManager.Describe(EnergySaverManager.GetState()));

				Thread.Sleep(1200);
				sb.AppendLine("    +1.2s = " + EnergySaverManager.Describe(EnergySaverManager.GetState()));

				sb.AppendLine();
				sb.AppendLine(">>> 还原到 " + baseline);
				string err2;
				bool ok2 = EnergySaverManager.SetEnabled(baseline == EnergySaverManager.STATE_ON, out err2);
				sb.AppendLine("    SetEnabled -> " + (ok2 ? "成功" : "失败")
							  + (err2 != null ? "  (" + err2 + ")" : ""));

				Thread.Sleep(1200);
				int fin = EnergySaverManager.GetState();
				sb.AppendLine("    最终 = " + fin + " (" + EnergySaverManager.Describe(fin) + ")");
				sb.AppendLine();
				sb.AppendLine(fin == baseline ? "结论: ✓ 通道可用，已还原" : "结论: ✗ 未还原，请手动检查");
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine("异常: " + ex);
		}

		sb.AppendLine();
		sb.AppendLine("[DONE]");

		try { System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8); }
		catch { }
	}

	private Icon CreateFluentIcon(string glyph, string colorHex, int size)	{
		Color color = ColorTranslator.FromHtml(colorHex);
		Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
		using (Graphics g = Graphics.FromImage(bitmap))
		{
			g.Clear(Color.Transparent);
			g.TextRenderingHint = TextRenderingHint.AntiAlias;
			g.SmoothingMode = SmoothingMode.HighQuality;
			g.CompositingQuality = CompositingQuality.HighQuality;
			float fontSize = size * 0.88f;
			using GraphicsPath path = new GraphicsPath();
			using Font font = new Font("Segoe Fluent Icons", fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
			using SolidBrush brush = new SolidBrush(color);
			StringFormat format = new StringFormat
			{
				Alignment = StringAlignment.Center,
				LineAlignment = StringAlignment.Center
			};
			path.AddString(glyph, font.FontFamily, (int)font.Style, fontSize, new RectangleF(0f, 0f, size, size), format);
			g.FillPath(brush, path);
			using Pen pen = new Pen(color, 1f);
			pen.LineJoin = LineJoin.Round;
			g.DrawPath(pen, path);
		}

		IntPtr hicon = bitmap.GetHicon();
		Icon icon = (Icon)Icon.FromHandle(hicon).Clone();
		DestroyIcon(hicon);
		bitmap.Dispose();
		return icon;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyIcon(IntPtr hIcon);

	private void CreateTrayIcon()
	{
		_tray = new Win32Tray();
		using (Icon icon = CreateTrayIconFromGlyph("\uE9CE"))
		{
			_tray.SetIcon(icon.Handle);
		}
		_tray.SetTooltip("HP 性能控制");
		_tray.LeftClicked += () => ShowMainWindow();
		_tray.MenuItemClicked += Tray_MenuItemClicked;
		BuildTrayMenu(false);
		_tray.Visible = true;
	}

	private Icon CreateTrayIconFromGlyph(string glyph)
	{
		try
		{
			return CreateFluentIcon(glyph, "#57c0ff", 64);
		}
		catch
		{
			try
			{
				return Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
			}
			catch
			{
				return SystemIcons.Application;
			}
		}
	}

	public void ShowMainWindow()
	{
		if (base.MainWindow != null)
		{
			base.MainWindow.Show();
			if (base.MainWindow.WindowState == WindowState.Minimized)
			{
				base.MainWindow.WindowState = WindowState.Normal;
			}
			base.MainWindow.Activate();
		}
	}

	private void BuildTrayMenu(bool connected, List<int> supportedModes = null, int currentMode = -1)
	{
		if (_tray == null)
		{
			return;
		}

		_tray.ClearMenuItems();
		_modeTags.Clear();

		if (connected && supportedModes != null)
		{
			if (supportedModes.Count > 0)
			{
				foreach (int mode in supportedModes)
				{
					string modeName = QuickHPControl.MainWindow.GetModeName(mode);
					bool isChecked = mode == currentMode;
					_tray.AddMenuItem(modeName, mode, isChecked);
					_modeTags.Add(mode);
				}
			}
			else
			{
				_tray.AddMenuItem("（无可用模式）", TAG_PLACEHOLDER, false, false, true);
			}
		}
		else
		{
			_tray.AddMenuItem("（加载中）", TAG_PLACEHOLDER, false, false, true);
		}

		_tray.AddMenuItem("", TAG_SEPARATOR, false, true);
		// 节能模式走 Windows WNF 通道，不依赖 BIOS 连接；未连接时也允许从托盘修改。
		_tray.AddMenuItem("系统节能模式", TAG_ENERGY_SAVER, _energySaverChecked, false, false);
		_tray.AddMenuItem("", TAG_SEPARATOR, false, true);
		_tray.AddMenuItem("显示界面", TAG_SHOW_WINDOW);
		_tray.AddMenuItem("", TAG_SEPARATOR, false, true);
		_tray.AddMenuItem("开机自启", TAG_AUTO_START, _autoStartChecked, false, !connected);
		_tray.AddMenuItem("", TAG_SEPARATOR, false, true);
		_tray.AddMenuItem("退出", TAG_EXIT);
	}

	public void UpdateModeMenuItems(List<int> supportedModes, int currentMode)
	{
		BuildTrayMenu(true, supportedModes, currentMode);
	}

	public void UpdateCurrentMode(int currentMode)
	{
		if (_tray == null)
		{
			return;
		}

		foreach (int tag in _modeTags)
		{
			_tray.SetMenuItemChecked(tag, tag == currentMode);
		}
		UpdateTrayIcon(currentMode);
		_tray.RefreshShowingMenu();
	}

	public void UpdateTrayIcon(int mode)
	{
		if (_tray == null || mode == _lastTrayMode)
		{
			return;
		}

		_lastTrayMode = mode;
		int generation = ++_trayIconGeneration;
		if (!QuickHPControl.MainWindow.ModeIcons.TryGetValue(mode, out var glyph))
		{
			glyph = "";
		}

		// GDI 字体/路径绘制不应占用 WPF Dispatcher；在高 DPI 或字体服务繁忙时，
		// CreateFluentIcon 可能明显变慢。只把最终 SetIcon 投回 UI 线程。
		_ = System.Threading.Tasks.Task.Run(() =>
		{
			Icon icon = CreateTrayIconFromGlyph(glyph);
			IntPtr handle = icon.Handle;
			try
			{
				Dispatcher.BeginInvoke(new Action(() =>
				{
					try
					{
						if (_tray != null && !_tray.IsDisposed && generation == _trayIconGeneration)
						{
							_tray.SetIcon(handle);
						}
					}
					finally
					{
						// SetIcon 内部已经 CopyIcon，之后才可以释放源 Icon。
						icon.Dispose();
					}
				}), DispatcherPriority.Background);
			}
			catch
			{
				icon.Dispose();
			}
		});
	}

	public void UpdateAutoStartMenuState(bool enabled)
	{
		_autoStartChecked = enabled;
		_tray?.SetMenuItemChecked(TAG_AUTO_START, enabled);
		_tray?.RefreshShowingMenu();
	}

	/// <summary>同步托盘菜单里「系统节能模式」的勾选状态。</summary>
	public void UpdateEnergySaverMenuState(bool enabled)
	{
		_energySaverChecked = enabled;
		_tray?.SetMenuItemChecked(TAG_ENERGY_SAVER, enabled);
		_tray?.RefreshShowingMenu();
	}

	private void Tray_MenuItemClicked(int tag)
	{
		if (tag >= 0)
		{
			HandleModeSelected(tag);
			return;
		}

		switch (tag)
		{
			case TAG_SHOW_WINDOW:
				ShowMainWindow();
				break;
			case TAG_ENERGY_SAVER:
				HandleEnergySaverToggled(!_energySaverChecked);
				break;
			case TAG_AUTO_START:
				HandleAutoStartToggled(!_autoStartChecked);
				break;
			case TAG_EXIT:
				ExitApplication();
				break;
		}
	}

	private void HandleModeSelected(int mode)
	{
		foreach (int tag in _modeTags)
		{
			_tray.SetMenuItemChecked(tag, tag == mode);
		}
		// BeginInvoke：托盘线程不等 UI，避免菜单「粘住」不消失
		base.Dispatcher.BeginInvoke(new Action(() =>
		{
			if (base.MainWindow is MainWindow mainWindow)
			{
				mainWindow.SetModeFromTray(mode);
			}
		}));
	}

	private void HandleEnergySaverToggled(bool newState)
	{
		_energySaverChecked = newState;
		_tray.SetMenuItemChecked(TAG_ENERGY_SAVER, newState);
		base.Dispatcher.BeginInvoke(new Action(() =>
		{
			if (base.MainWindow is MainWindow mainWindow)
			{
				mainWindow.SetEnergySaverFromTray(newState);
			}
		}));
	}

	private void HandleAutoStartToggled(bool newState)
	{
		_autoStartChecked = newState;
		_tray.SetMenuItemChecked(TAG_AUTO_START, newState);
		base.Dispatcher.BeginInvoke(new Action(() =>
		{
			if (base.MainWindow is MainWindow mainWindow)
			{
				mainWindow.ToggleAutoStartFromTray(newState);
			}
		}));
	}

	private void ExitApplication()
	{
		if (base.MainWindow is MainWindow mw)
		{
			mw.PrepareForExit();
		}
		_tray.Visible = false;
		_tray.Dispose();
		_tray = null;
		Shutdown();
	}

	private static void SignalExistingInstance()
	{
		try
		{
			using EventWaitHandle evt = EventWaitHandle.OpenExisting("Global\\QuickHPControl_ShowWindow");
			evt.Set();
		}
		catch
		{
		}
	}

	private void StartShowWindowListener()
	{
		_showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\QuickHPControl_ShowWindow");
		Thread thread = new Thread(() =>
		{
			while (_showWindowEvent != null)
			{
				try
				{
					if (_showWindowEvent.WaitOne())
					{
						base.Dispatcher.Invoke(() => ShowMainWindow());
					}
				}
				catch (ObjectDisposedException)
				{
					break;
				}
			}
		});
		thread.IsBackground = true;
		thread.Name = "ShowWindowListener";
		thread.Start();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_tray?.Dispose();
		_tray = null;
		_showWindowEvent?.Set();
		_showWindowEvent?.Dispose();
		_showWindowEvent = null;
		_singleInstanceMutex?.ReleaseMutex();
		_singleInstanceMutex?.Dispose();
		_singleInstanceMutex = null;
		base.OnExit(e);
	}

}
