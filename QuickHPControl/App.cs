using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickHPControl;

public partial class App : Application
{
	private enum PROCESS_INFORMATION_CLASS
	{
		ProcessPowerThrottling = 4
	}

	private struct PROCESS_POWER_THROTTLING_STATE
	{
		public uint Version;
		public uint ControlMask;
		public uint StateMask;
	}

	private Win32Tray _tray;
	private int _lastTrayMode = -1;
	private int _currentMode = -1;
	private readonly HashSet<int> _modeTags = new HashSet<int>();

	private const int TAG_AUTO_START = -1;
	private const int TAG_EXIT = -3;
	private const int TAG_SHOW_WINDOW = -4;
	private const int TAG_PLACEHOLDER = -99;
	private const int TAG_SEPARATOR = -10;
	private const string TRAY_GLYPH = "\uE9CE";
	private const string TRAY_ICON_COLOR = "#57c0ff";
	private const int TRAY_ICON_SIZE = 64;
	private const string TASK_NAME = "QuickHPControlAutoStart";
	private const string HIDE_ARG = "--hide";

	private Mutex _singleInstanceMutex;
	private EventWaitHandle _showWindowEvent;
	private const string MUTEX_NAME = "Global\\QuickHPControl_SingleInstance";
	private const string SHOW_EVENT_NAME = "Global\\QuickHPControl_ShowWindow";
	private bool _autoStartChecked;

	private const uint PROCESS_SET_INFORMATION = 512u;
	private const uint PROCESS_QUERY_INFORMATION = 1024u;
	private const uint IDLE_PRIORITY_CLASS = 64u;
	private const int PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;
	private const int PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;

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

	public App()
	{
		AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
		{
			if (new AssemblyName(args.Name).Name == "Newtonsoft.Json")
			{
				string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Newtonsoft.Json.dll");
				if (File.Exists(path))
				{
					return Assembly.LoadFrom(path);
				}
			}
			return null;
		};
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
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
		SetSelfPowerSaver();

		base.DispatcherUnhandledException += (s, args) => args.Handled = true;
		AppDomain.CurrentDomain.UnhandledException += (s, args) => { _ = args.ExceptionObject; };

		base.MainWindow = new MainWindow();
		CreateTrayIcon();
		StartShowWindowListener();

		if (!IsHideOnStartup())
		{
			base.MainWindow.Show();
		}

		_ = ((MainWindow)base.MainWindow).TryAutoConnect();
	}

	private Icon CreateFluentIcon(string glyph, string colorHex, int size)
	{
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
				_tray.AddMenuItem("（无可用模式）", -99, false, false, true);
			}
		}
		else
		{
			_tray.AddMenuItem("（加载中）", -99, false, false, true);
		}

		_tray.AddMenuItem("", -10, false, true);
		_tray.AddMenuItem("显示界面", -4);
		_tray.AddMenuItem("", -10, false, true);
		_tray.AddMenuItem("开机自启", -1, _autoStartChecked, false, !connected);
		_tray.AddMenuItem("", -10, false, true);
		_tray.AddMenuItem("退出", -3);
	}

	public void UpdateModeMenuItems(List<int> supportedModes, int currentMode)
	{
		_currentMode = currentMode;
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
		_currentMode = currentMode;
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
		if (!QuickHPControl.MainWindow.ModeIcons.TryGetValue(mode, out var glyph))
		{
			glyph = "";
		}
		using Icon icon = CreateTrayIconFromGlyph(glyph);
		_tray.SetIcon(icon.Handle);
	}

	public void UpdateAutoStartMenuState(bool enabled)
	{
		_autoStartChecked = enabled;
		_tray?.SetMenuItemChecked(-1, enabled);
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
			case -4:
				ShowMainWindow();
				break;
			case -1:
				HandleAutoStartToggled(!_autoStartChecked);
				break;
			case -3:
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
		_currentMode = mode;
		base.Dispatcher.Invoke(() =>
		{
			if (base.MainWindow is MainWindow mainWindow)
			{
				mainWindow.SetModeFromTray(mode);
			}
		});
	}

	private void HandleAutoStartToggled(bool newState)
	{
		_autoStartChecked = newState;
		_tray.SetMenuItemChecked(-1, newState);
		base.Dispatcher.Invoke(() =>
		{
			if (base.MainWindow is MainWindow mainWindow)
			{
				mainWindow.ToggleAutoStartFromTray(newState);
			}
		});
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

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetProcessInformation(IntPtr hProcess, PROCESS_INFORMATION_CLASS ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

	private static void SetSelfPowerSaver()
	{
		IntPtr hProcess = IntPtr.Zero;
		try
		{
			int pid = Process.GetCurrentProcess().Id;
			hProcess = OpenProcess(1536u, false, pid);
			if (hProcess == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}

			PROCESS_POWER_THROTTLING_STATE powerState = new PROCESS_POWER_THROTTLING_STATE
			{
				Version = 1u,
				ControlMask = 1u,
				StateMask = 1u
			};

			if (!SetProcessInformation(hProcess, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, ref powerState, Marshal.SizeOf(powerState)))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}

			if (!SetPriorityClass(hProcess, 64u))
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
		}
		catch (Win32Exception)
		{
		}
		finally
		{
			if (hProcess != IntPtr.Zero)
			{
				CloseHandle(hProcess);
			}
		}
	}
}
