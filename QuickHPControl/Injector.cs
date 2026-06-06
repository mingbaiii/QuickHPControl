using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickHPControl;

public static class Injector
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MODULEENTRY32W
	{
		public uint dwSize;
		public uint th32ModuleID;
		public uint th32ProcessID;
		public uint GlblcntUsage;
		public uint ProccntUsage;
		public IntPtr modBaseAddr;
		public uint modBaseSize;
		public IntPtr hModule;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szModule;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szExePath;
	}

	private const uint PROCESS_CREATE_THREAD = 0x0002;
	private const uint PROCESS_QUERY_INFORMATION = 0x0400;
	private const uint PROCESS_VM_OPERATION = 0x0008;
	private const uint PROCESS_VM_WRITE = 0x0020;
	private const uint PROCESS_VM_READ = 0x0010;
	private const uint PROCESS_DUP_HANDLE = 0x0040;
	private const uint MEM_COMMIT = 0x1000;
	private const uint MEM_RESERVE = 0x2000;
	private const uint PAGE_READWRITE = 0x04;
	private const uint INFINITE = 0xFFFFFFFF;
	private const int INJECT_TIMEOUT = 15000;
	private const string BOOTSTRAP_DLL_NAME = "BootstrapNative.dll";
	private const uint TH32CS_SNAPMODULE = 0x00000008;
	private const uint TH32CS_SNAPMODULE32 = 0x00000010;
	private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
	private const uint MEM_RELEASE = 0x8000;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FreeLibrary(IntPtr hModule);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

	private static uint PROCESS_ALL_ACCESS => PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | PROCESS_DUP_HANDLE;

	public static bool Inject(Process target, string bootstrapDllPath, Action<string> log = null)
	{
		void Log(string msg) => log?.Invoke(msg);

		Log("正在打开目标进程 PID=" + target.Id + " ...");
		IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, target.Id);
		if (hProcess == IntPtr.Zero)
		{
			Log("OpenProcess 失败, 错误码: " + Marshal.GetLastWin32Error());
			return false;
		}

		try
		{
			IntPtr targetModuleBase = FindModuleInTarget(target, BOOTSTRAP_DLL_NAME);
			if (targetModuleBase != IntPtr.Zero)
			{
				Log("BootstrapNative.dll 已驻留 (base=0x" + targetModuleBase.ToString("X") + ")，跳过注入");
				return true;
			}

			Log("DLL 未驻留，执行 LoadLibraryW 注入...");
			IntPtr loadLibAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
			if (loadLibAddr == IntPtr.Zero)
			{
				Log("GetProcAddress(LoadLibraryW) 失败");
				return false;
			}

			Log("LoadLibraryW 地址: 0x" + loadLibAddr.ToString("X"));

			byte[] pathBytes = Encoding.Unicode.GetBytes(bootstrapDllPath + "\0");
			uint pathSize = (uint)pathBytes.Length;
			IntPtr allocAddr = VirtualAllocEx(hProcess, IntPtr.Zero, pathSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
			if (allocAddr == IntPtr.Zero)
			{
				Log("VirtualAllocEx 失败, 错误码: " + Marshal.GetLastWin32Error());
				return false;
			}

			Log("已在目标进程分配内存: 0x" + allocAddr.ToString("X"));

			if (!WriteProcessMemory(hProcess, allocAddr, pathBytes, pathSize, out var bytesWritten))
			{
				Log("WriteProcessMemory 失败, 错误码: " + Marshal.GetLastWin32Error());
				return false;
			}

			Log("已写入 " + bytesWritten + " 字节");

			IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibAddr, allocAddr, 0, IntPtr.Zero);
			if (hThread == IntPtr.Zero)
			{
				Log("CreateRemoteThread 失败, 错误码: " + Marshal.GetLastWin32Error());
				return false;
			}

			Log("远程线程已创建, 等待完成...");
			uint waitResult = WaitForSingleObject(hThread, INJECT_TIMEOUT);
			switch (waitResult)
			{
				case 0:
					if (GetExitCodeThread(hThread, out var exitCode))
					{
						Log("LoadLibraryW 返回: 0x" + exitCode.ToString("X"));
					}
					break;
				case 258:
					Log($"注入超时 ({INJECT_TIMEOUT}ms)");
					break;
				default:
					Log("WaitForSingleObject 返回: 0x" + waitResult.ToString("X"));
					break;
			}

			CloseHandle(hThread);
			return true;
		}
		finally
		{
			CloseHandle(hProcess);
		}
	}

	private static IntPtr FindModuleInTarget(Process target, string moduleName)
	{
		IntPtr hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)target.Id);
		if (hSnapshot == IntPtr.Zero || hSnapshot == (IntPtr)(-1))
		{
			return IntPtr.Zero;
		}

		try
		{
			MODULEENTRY32W me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
			if (Module32FirstW(hSnapshot, ref me))
			{
				do
				{
					if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
					{
						return me.modBaseAddr;
					}
				}
				while (Module32NextW(hSnapshot, ref me));
			}
		}
		finally
		{
			CloseHandle(hSnapshot);
		}

		return IntPtr.Zero;
	}

	private static bool CallBootstrapStartInTarget(IntPtr hProcess, IntPtr targetModuleBase, string bootstrapDllPath, Action<string> log)
	{
		IntPtr localModule = LoadLibraryEx(bootstrapDllPath, IntPtr.Zero, LOAD_LIBRARY_AS_IMAGE_RESOURCE);
		if (localModule == IntPtr.Zero)
		{
			log("LoadLibraryEx(AS_IMAGE_RESOURCE) 失败, 错误码: " + Marshal.GetLastWin32Error());
			return false;
		}

		try
		{
			IntPtr localFunc = GetProcAddress(localModule, "BootstrapStart");
			if (localFunc == IntPtr.Zero)
			{
				log("GetProcAddress(BootstrapStart) 失败 — DLL 可能未重新编译");
				return false;
			}

			long rva = localFunc.ToInt64() - localModule.ToInt64();
			log("BootstrapStart RVA = 0x" + rva.ToString("X"));

			IntPtr remoteFunc = new IntPtr(targetModuleBase.ToInt64() + rva);
			log("远程 BootstrapStart 地址 = 0x" + remoteFunc.ToString("X"));

			IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteFunc, IntPtr.Zero, 0, IntPtr.Zero);
			if (hThread == IntPtr.Zero)
			{
				log("CreateRemoteThread(BootstrapStart) 失败, 错误码: " + Marshal.GetLastWin32Error());
				return false;
			}

			log("BootstrapStart 远程线程已创建, 等待完成...");
			uint waitResult = WaitForSingleObject(hThread, INJECT_TIMEOUT);
			if (waitResult == 0)
			{
				GetExitCodeThread(hThread, out var exitCode);
				log("BootstrapStart 返回: 0x" + exitCode.ToString("X"));
			}
			else
			{
				log("BootstrapStart 等待返回: 0x" + waitResult.ToString("X"));
			}

			CloseHandle(hThread);
			return true;
		}
		finally
		{
			FreeLibrary(localModule);
		}
	}

	public static Process FindTargetProcess()
	{
		Process[] procs = Process.GetProcessesByName("HP.SystemControl.Background");
		return procs.Length > 0 ? procs[0] : null;
	}

	public static bool Unload(Process target, string dllName, Action<string> log = null)
	{
		void Log(string msg) => log?.Invoke(msg);

		Log("正在打开目标进程 PID=" + target.Id + " (卸载)...");
		IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, target.Id);
		if (hProcess == IntPtr.Zero)
		{
			Log("OpenProcess(Unload) 失败, 错误码: " + Marshal.GetLastWin32Error());
			return false;
		}

		try
		{
			IntPtr hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)target.Id);
			if (hSnapshot == IntPtr.Zero || hSnapshot == (IntPtr)(-1))
			{
				Log("CreateToolhelp32Snapshot 失败");
				return false;
			}

			IntPtr hModule = IntPtr.Zero;
			try
			{
				MODULEENTRY32W me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
				if (Module32FirstW(hSnapshot, ref me))
				{
					do
					{
						if (string.Equals(me.szModule, dllName, StringComparison.OrdinalIgnoreCase))
						{
							hModule = me.hModule;
							Log("找到模块 " + dllName + " hModule=0x" + hModule.ToString("X"));
							break;
						}
					}
					while (Module32NextW(hSnapshot, ref me));
				}
			}
			finally
			{
				CloseHandle(hSnapshot);
			}

			if (hModule == IntPtr.Zero)
			{
				Log("未在目标进程中找到模块: " + dllName);
				return false;
			}

			IntPtr freeLibAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "FreeLibrary");
			if (freeLibAddr == IntPtr.Zero)
			{
				Log("GetProcAddress(FreeLibrary) 失败");
				return false;
			}

			IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, freeLibAddr, hModule, 0, IntPtr.Zero);
			if (hThread == IntPtr.Zero)
			{
				Log("CreateRemoteThread(FreeLibrary) 失败, 错误码: " + Marshal.GetLastWin32Error());
				return false;
			}

			Log("FreeLibrary 远程线程已创建, 等待完成...");
			uint waitResult = WaitForSingleObject(hThread, 5000);
			if (waitResult == 0)
			{
				GetExitCodeThread(hThread, out var exitCode);
				Log("FreeLibrary 完成, 退出码: " + exitCode);
			}
			else
			{
				Log("FreeLibrary 等待返回: 0x" + waitResult.ToString("X"));
			}

			CloseHandle(hThread);
			return true;
		}
		finally
		{
			CloseHandle(hProcess);
		}
	}
}
