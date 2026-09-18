using System;
using System.Runtime.InteropServices;

namespace QuickHPControl;

/// <summary>
/// Windows 11「电源模式」滑块（不是 powercfg 的电源计划）。
///
/// 对应 设置 → 系统 → 电源和电池 → 电源模式 的三档：
///   最佳性能 / 平衡 / 最佳能效
///
/// 底层 API：powrprof.dll!PowerGet/SetUserConfigured{AC,DC}PowerMode
///
/// 为什么需要它：HP 的后台服务会双向同步「Windows 电源模式」与「BIOS 热控模式」。
/// 我们直写 BIOS 后如果不把滑块一起同步，HP 服务检测到两者不一致时可能把模式改回去。
/// </summary>
public static class PowerPlanManager
{
	public static readonly Guid BEST_PERFORMANCE = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
	public static readonly Guid BALANCED = Guid.Empty;
	public static readonly Guid BEST_POWER_EFFICIENCY = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetUserConfiguredACPowerMode(out Guid guid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid guid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid guid);

	[DllImport("powrprof.dll")]
	private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid guid);

	[DllImport("kernel32.dll")]
	private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

	[StructLayout(LayoutKind.Sequential)]
	private struct SYSTEM_POWER_STATUS
	{
		public byte ACLineStatus;      // 0=电池, 1=交流电, 255=未知
		public byte BatteryFlag;
		public byte BatteryLifePercent;
		public byte SystemStatusFlag;
		public int BatteryLifeTime;
		public int BatteryFullLifeTime;
	}

	/// <summary>当前是否在用电池供电（决定读写 AC 还是 DC 的配置）。</summary>
	public static bool IsOnBattery()
	{
		try
		{
			if (GetSystemPowerStatus(out var sps))
			{
				return sps.ACLineStatus == 0;
			}
		}
		catch { }
		return false;
	}

	public static bool GetCurrent(bool isOnBattery, out Guid guid)
	{
		uint r = isOnBattery
			? PowerGetUserConfiguredDCPowerMode(out guid)
			: PowerGetUserConfiguredACPowerMode(out guid);
		return r == 0;
	}

	public static bool Set(bool isOnBattery, Guid guid)
	{
		uint r = isOnBattery
			? PowerSetUserConfiguredDCPowerMode(ref guid)
			: PowerSetUserConfiguredACPowerMode(ref guid);
		return r == 0;
	}

	/// <summary>把滑块切到与热控模式对应的档位。</summary>
	public static bool SetForMode(int mode, bool isOnBattery)
	{
		Guid target;
		switch (mode)
		{
			case 0: target = BEST_PERFORMANCE; break;
			case 4: target = BEST_POWER_EFFICIENCY; break;   // PowerSaver
			default: target = BALANCED; break;
		}

		Guid cur;
		if (GetCurrent(isOnBattery, out cur) && cur == target)
		{
			return true;
		}

		return Set(isOnBattery, target);
	}

	/// <summary>V2 下滑块只区分三大类：0=Performance, 4=PowerSaver, -1=Balanced 系列（需回 BIOS 取子模式）。</summary>
	public static int GetCategoryFromGuid(Guid guid)
	{
		if (guid == BEST_PERFORMANCE) return 0;
		if (guid == BEST_POWER_EFFICIENCY) return 4;
		return -1;
	}

	/// <summary>读当前滑块档位名（仅用于界面展示，失败返回 "未知"）。</summary>
	public static string GetSliderName()
	{
		try
		{
			if (!GetCurrent(IsOnBattery(), out Guid guid))
			{
				return "未知";
			}
			if (guid == BEST_PERFORMANCE) return "最佳性能";
			if (guid == BEST_POWER_EFFICIENCY) return "最佳能效";
			if (guid == BALANCED) return "平衡";
			return "自定义";
		}
		catch
		{
			return "未知";
		}
	}
}
