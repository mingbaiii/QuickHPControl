using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickHPControl;

public class Win32Tray : IDisposable
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NOTIFYICONDATAW
	{
		public uint cbSize;
		public IntPtr hWnd;
		public uint uID;
		public uint uFlags;
		public uint uCallbackMessage;
		public IntPtr hIcon;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;

		public uint dwState;
		public uint dwStateMask;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szInfo;

		public uint uTimeoutOrVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string szInfoTitle;

		public uint dwInfoFlags;
	}

	private struct POINT
	{
		public int x;
		public int y;
	}

	private enum PreferredAppMode
	{
		Default,
		AllowDark,
		ForceDark,
		ForceLight,
		Max
	}

	private const uint NIM_ADD = 0x00000000;
	private const uint NIM_MODIFY = 0x00000001;
	private const uint NIM_DELETE = 0x00000002;
	private const uint NIF_MESSAGE = 0x00000001;
	private const uint NIF_ICON = 0x00000002;
	private const uint NIF_TIP = 0x00000004;
	private const uint NIF_STATE = 0x00000008;
	private const uint NIF_INFO = 0x00000010;
	private const uint NIS_HIDDEN = 0x00000001;
	private const uint NIS_SHAREDICON = 0x00000002;
	private const uint WM_APP = 0x8000;
	private const uint WM_TRAYICON = WM_APP + 200;
	private const uint WM_SHOWCONTEXTMENU = WM_APP + 201;
	private const uint WM_LBUTTONUP = 0x0202;
	private const uint WM_RBUTTONUP = 0x0205;
	private const uint WM_LBUTTONDBLCLK = 0x0203;
	private const uint WM_CONTEXTMENU = 0x007B;
	private const uint WM_COMMAND = 0x0111;
	private const uint MF_STRING = 0x00000000;
	private const uint MF_SEPARATOR = 0x00000800;
	private const uint MF_CHECKED = 0x00000008;
	private const uint MF_UNCHECKED = 0x00000000;
	private const uint MF_GRAYED = 0x00000001;
	private const uint MF_DISABLED = 0x00000002;
	private const uint MF_ENABLED = 0x00000000;
	private const uint MF_BYCOMMAND = 0x00000000;
	private const uint MF_BYPOSITION = 0x00000400;
	private const uint TPM_LEFTALIGN = 0x00000000;
	private const uint TPM_RIGHTBUTTON = 0x00000002;
	private const uint TPM_BOTTOMALIGN = 0x00000020;
	private const uint TPM_LEFTBUTTON = 0x00000000;
	private const uint WM_CANCELMODE = 0x001F;

	private HwndSource _hwndSource;
	private IntPtr _hWnd;
	private NOTIFYICONDATAW _nid;
	private IntPtr _hPopupMenu;
	private IntPtr _hCurrentIcon;
	private bool _visible;
	private bool _disposed;
	private string _tooltip = "";
	private bool _isMenuShowing;
	private bool _useLastPos;
	private POINT _lastPopupPos;
	private bool _menuDirty;
	private readonly Dictionary<int, int> _tagToId = new Dictionary<int, int>();
	private readonly Dictionary<int, int> _idToTag = new Dictionary<int, int>();
	private int _nextMenuId = 1000;
	private readonly Dictionary<int, bool> _checkedState = new Dictionary<int, bool>();
	private readonly Dictionary<int, bool> _enabledState = new Dictionary<int, bool>();
	private readonly uint _taskbarRestartMsg;

	public bool Visible
	{
		get => _visible;
		set
		{
			if (value != _visible)
			{
				if (value) AddTrayIcon();
				else RemoveTrayIcon();
			}
		}
	}

	public event Action<int> MenuItemClicked;
	public event Action LeftClicked;

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyMenu(IntPtr hMenu);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint RegisterWindowMessageW(string lpString);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyIcon(IntPtr hIcon);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	[DllImport("uxtheme.dll", EntryPoint = "#135")]
	private static extern PreferredAppMode SetPreferredAppMode(PreferredAppMode appMode);

	[DllImport("uxtheme.dll", EntryPoint = "#133")]
	private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CopyIcon(IntPtr hIcon);

	public Win32Tray()
	{
		try { SetPreferredAppMode(PreferredAppMode.AllowDark); } catch { }

		_taskbarRestartMsg = RegisterWindowMessageW("TaskbarCreated");

		HwndSourceParameters parameters = new HwndSourceParameters("Win32TrayHost")
		{
			WindowStyle = 0,
			ExtendedWindowStyle = 0x80,
			Width = 0,
			Height = 0
		};

		_hwndSource = new HwndSource(parameters);
		_hwndSource.AddHook(WndProc);
		_hWnd = _hwndSource.Handle;

		try { AllowDarkModeForWindow(_hWnd, true); } catch { }

		_nid = new NOTIFYICONDATAW
		{
			cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
			hWnd = _hWnd,
			uID = 1,
			uFlags = NIF_MESSAGE | NIF_TIP,
			uCallbackMessage = WM_TRAYICON,
			szTip = ""
		};

		_hPopupMenu = CreatePopupMenu();
	}

	~Win32Tray() => Dispose(false);

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposed)
		{
			_disposed = true;
			if (_visible)
			{
				Shell_NotifyIconW(NIM_DELETE, ref _nid);
				_visible = false;
			}

			DestroyCurrentIcon();

			if (_hPopupMenu != IntPtr.Zero)
			{
				DestroyMenu(_hPopupMenu);
				_hPopupMenu = IntPtr.Zero;
			}

			if (_hwndSource != null)
			{
				_hwndSource.RemoveHook(WndProc);
				_hwndSource.Dispose();
				_hwndSource = null;
			}

			_tagToId.Clear();
			_idToTag.Clear();
		}
	}

	public void SetIcon(IntPtr hIcon)
	{
		DestroyCurrentIcon();
		_hCurrentIcon = CopyIcon(hIcon);
		_nid.hIcon = _hCurrentIcon;
		_nid.uFlags |= NIF_ICON;
		if (_visible)
		{
			Shell_NotifyIconW(NIM_MODIFY, ref _nid);
		}
	}

	public void SetTooltip(string text)
	{
		_tooltip = text ?? "";
		_nid.szTip = _tooltip.Length > 127 ? _tooltip.Substring(0, 127) : _tooltip;
		_nid.uFlags |= NIF_TIP;
		if (_visible)
		{
			Shell_NotifyIconW(NIM_MODIFY, ref _nid);
		}
	}

	public void AddMenuItem(string text, int tag, bool isChecked = false, bool isSeparator = false, bool isDisabled = false)
	{
		if (_disposed) return;

		int id = _nextMenuId++;
		_tagToId[tag] = id;
		_idToTag[id] = tag;
		_checkedState[tag] = isChecked;
		_enabledState[tag] = !isDisabled;
		_menuDirty = true;

		if (isSeparator)
		{
			AppendMenuW(_hPopupMenu, MF_SEPARATOR, (uint)id, null);
			return;
		}

		uint flags = MF_STRING;
		if (isChecked) flags |= MF_CHECKED;
		if (isDisabled) flags |= MF_GRAYED;
		AppendMenuW(_hPopupMenu, flags, (uint)id, text);
	}

	public void ClearMenuItems()
	{
		if (_disposed) return;

		_tagToId.Clear();
		_idToTag.Clear();
		_checkedState.Clear();
		_enabledState.Clear();
		_nextMenuId = 1000;
		_menuDirty = true;

		if (_hPopupMenu != IntPtr.Zero)
		{
			DestroyMenu(_hPopupMenu);
			_hPopupMenu = CreatePopupMenu();
		}
	}

	public void SetMenuItemChecked(int tag, bool isChecked)
	{
		if (_disposed || !_tagToId.TryGetValue(tag, out var id)) return;
		if (_checkedState.TryGetValue(tag, out var current) && current == isChecked) return;

		CheckMenuItem(_hPopupMenu, (uint)id, MF_BYCOMMAND | (isChecked ? MF_CHECKED : MF_UNCHECKED));
		_checkedState[tag] = isChecked;
		_menuDirty = true;
	}

	public void SetMenuItemEnabled(int tag, bool enabled)
	{
		if (_disposed || !_tagToId.TryGetValue(tag, out var id)) return;
		if (_enabledState.TryGetValue(tag, out var current) && current == enabled) return;

		EnableMenuItem(_hPopupMenu, (uint)id, MF_BYCOMMAND | (enabled ? MF_ENABLED : MF_GRAYED));
		_enabledState[tag] = enabled;
		_menuDirty = true;
	}

	public void RefreshShowingMenu()
	{
		if (_disposed || !_menuDirty) return;

		_menuDirty = false;
		if (_isMenuShowing)
		{
			PostMessageW(_hWnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
			_useLastPos = true;
			PostMessageW(_hWnd, WM_SHOWCONTEXTMENU, IntPtr.Zero, IntPtr.Zero);
		}
	}

	private void AddTrayIcon()
	{
		if (_disposed) return;

		if (string.IsNullOrEmpty(_nid.szTip))
		{
			_nid.szTip = "HP 性能控制";
		}

		if (!Shell_NotifyIconW(NIM_ADD, ref _nid))
		{
			Marshal.GetLastWin32Error();
		}
		else
		{
			_visible = true;
		}
	}

	private void RemoveTrayIcon()
	{
		if (_visible && !_disposed)
		{
			Shell_NotifyIconW(NIM_DELETE, ref _nid);
			_visible = false;
		}
	}

	private void DestroyCurrentIcon()
	{
		if (_hCurrentIcon != IntPtr.Zero)
		{
			DestroyIcon(_hCurrentIcon);
			_hCurrentIcon = IntPtr.Zero;
		}
	}

	private void ShowContextMenu()
	{
		if (_disposed || _hPopupMenu == IntPtr.Zero) return;

		_menuDirty = false;
		int x, y;

		if (_useLastPos)
		{
			_useLastPos = false;
			x = _lastPopupPos.x;
			y = _lastPopupPos.y;
		}
		else
		{
			if (!GetCursorPos(out var pt)) return;
			_lastPopupPos = pt;
			x = pt.x;
			y = pt.y;
		}

		TrackAndShowMenu(x, y);
	}

	private void TrackAndShowMenu(int x, int y)
	{
		_isMenuShowing = true;
		try
		{
			SetForegroundWindow(_hWnd);
			TrackPopupMenu(_hPopupMenu, TPM_BOTTOMALIGN | TPM_LEFTALIGN | TPM_RIGHTBUTTON, x, y, 0, _hWnd, IntPtr.Zero);
		}
		finally
		{
			_isMenuShowing = false;
		}
	}

	private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == (int)_taskbarRestartMsg)
		{
			if (_visible)
			{
				_visible = false;
				AddTrayIcon();
			}
			handled = true;
			return IntPtr.Zero;
		}

		switch ((uint)msg)
		{
			case WM_TRAYICON:
				switch ((uint)lParam.ToInt64())
				{
					case WM_LBUTTONUP:
					case WM_LBUTTONDBLCLK:
						LeftClicked?.Invoke();
						handled = true;
						break;
					case WM_CONTEXTMENU:
					case WM_RBUTTONUP:
						if (_isMenuShowing)
						{
							PostMessageW(_hWnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
						}
						PostMessageW(_hWnd, WM_SHOWCONTEXTMENU, IntPtr.Zero, IntPtr.Zero);
						handled = true;
						break;
				}
				return IntPtr.Zero;

			case WM_SHOWCONTEXTMENU:
				ShowContextMenu();
				handled = true;
				return IntPtr.Zero;

			case WM_COMMAND:
				int itemId = (int)(wParam.ToInt64() & 0xFFFF);
				if (_idToTag.TryGetValue(itemId, out var tag))
				{
					MenuItemClicked?.Invoke(tag);
				}
				handled = true;
				return IntPtr.Zero;

			default:
				return IntPtr.Zero;
		}
	}
}
