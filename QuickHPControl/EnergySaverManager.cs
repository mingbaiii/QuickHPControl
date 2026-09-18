using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace QuickHPControl;

/// <summary>
/// Windows 11 24H2+ 的「节能模式」（Energy Saver）开关。
///
/// 就是 设置 → 系统 → 电源和电池 → 节能模式 那个开关（副标题「减少能耗并延长电池寿命」）。
/// 注意它不是旧版「节电模式」（Battery Saver），也不是「电源模式」滑块。
///
/// ────────────────────────────────────────────────────────────────
/// 实现原理（反汇编取证，见 hp-bios/README.md 第 11 节）
/// ────────────────────────────────────────────────────────────────
/// 设置界面点这个开关时，真正干活的是
///   C:\Windows\System32\SettingsHandlers_OneCore_BatterySaver.dll
/// 它做两件事：
///   1. 调 RtlPublishWnfStateData 广播一个 WNF 状态（**这才是触发信号**）
///   2. 顺手把结果写进 HKLM\...\Control\Power\EnergySaverState
///
/// 关键点：注册表值是**结果**，不是**输入**。
/// 只写注册表不会有任何效果（电源服务不会重新求值）—— 必须发 WNF 广播。
///
/// WNF 状态名 0x41C6013DA3BC3075，负载是 4 字节 DWORD：
///   1 = 开
///   2 = 关
/// 该 WNF 家族有 29 个监听者：umpo.dll（电源服务）、umpoext.dll、energyprov.dll、
/// ntoskrnl.exe、batmeter.dll、schedsvc.dll、taskcomp.dll、
/// ContentDeliveryManager.Utilities.dll、diagtrack.dll、stobject.dll、SystemTray.dll …
/// 所以广播一次就能让「限制后台应用 / 延迟计划任务 / 停遥测」等行为一起生效。
///
/// 实测（普通用户态，无需提权）：
///   发布 1 → EnergySaverState=1，effective overlay 变为 961CC777（Better Battery-life）
///   发布 2 → EnergySaverState=2，effective overlay 恢复用户值
///
/// ────────────────────────────────────────────────────────────────
/// 本类不碰的相关项
/// ────────────────────────────────────────────────────────────────
///   · SUB_ENERGYSAVER 子组 (de830923-…) 下的阈值/策略/亮度权重 —— 是「开启后做什么」的配置
///   · Policies\Microsoft\Power\EnergySaver\EnableEnergySaver —— 组策略「始终开启」
///   · 电源模式滑块（overlay）—— 由 PowerPlanManager 负责，与节能模式是两条独立通道
/// </summary>
public static class EnergySaverManager
{
	private const string POWER_KEY = @"SYSTEM\CurrentControlSet\Control\Power";
	private const string STATE_VALUE = "EnergySaverState";

	public const int STATE_ON = 1;
	public const int STATE_OFF = 2;

	/// <summary>
	/// 节能模式状态的 WNF 状态名。
	/// 来源：SettingsHandlers_OneCore_BatterySaver.dll 的 .rdata 偏移 0x5B970。
	/// </summary>
	private const ulong WNF_ENERGY_SAVER_STATE = 0x41C6013DA3BC3075UL;

	// NTSTATUS RtlPublishWnfStateData(
	//     WNF_STATE_NAME StateName, WNF_TYPE_ID TypeId, const VOID *Buffer,
	//     ULONG Length, ULONG ChangeStamp);
	[DllImport("ntdll.dll", ExactSpelling = true)]
	private static extern int RtlPublishWnfStateData(
		ulong stateName, IntPtr typeId, ref uint buffer, uint length, uint changeStamp);

	private const int STATUS_SUCCESS = 0;

	// ================================================================
	//  读
	// ================================================================

	/// <summary>读当前状态：1=开，2=关，-1=读取失败。</summary>
	public static int GetState()
	{
		try
		{
			using RegistryKey key = Registry.LocalMachine.OpenSubKey(POWER_KEY, false);
			if (key == null) return -1;

			object raw = key.GetValue(STATE_VALUE);
			if (raw == null) return STATE_OFF;   // 值不存在时按「关」处理

			return Convert.ToInt32(raw);
		}
		catch
		{
			return -1;
		}
	}

	public static bool IsOn() => GetState() == STATE_ON;

	// ================================================================
	//  写（走 WNF 通道）
	// ================================================================

	/// <summary>
	/// 通过 WNF 广播设置节能模式状态。**不需要管理员权限。**
	/// 调用成功后系统自己会把 EnergySaverState 和 effective overlay 一起更新。
	///
	/// 本方法**不做任何等待**：只回报 WNF 广播本身是否被接受。
	/// 电源服务是异步处理的，注册表值要过几十到几百毫秒才跟上；
	/// 需要确认结果请另外调用 <see cref="WaitForState"/>（只用于自检/诊断，
	/// 绝不要在持有锁或 UI 线程上调用）。
	/// </summary>
	public static bool SetState(int state, out string error)
	{
		error = null;

		if (state != STATE_ON && state != STATE_OFF)
		{
			error = "非法状态值: " + state;
			return false;
		}

		uint payload = (uint)state;
		int status;

		try
		{
			status = RtlPublishWnfStateData(
				WNF_ENERGY_SAVER_STATE,
				IntPtr.Zero,
				ref payload,
				4,
				0);
		}
		catch (Exception ex)
		{
			error = "调用 RtlPublishWnfStateData 异常: " + ex.Message;
			return false;
		}

		if (status != STATUS_SUCCESS)
		{
			error = "RtlPublishWnfStateData 返回 0x" + ((uint)status).ToString("X8")
					+ "（" + DescribeNtStatus((uint)status) + "）";
			return false;
		}

		return true;
	}

	/// <summary>开/关节能模式（同样不做等待）。</summary>
	public static bool SetEnabled(bool on, out string error)
	{
		return SetState(on ? STATE_ON : STATE_OFF, out error);
	}

	/// <summary>
	/// 轮询等待注册表跟上（最长 timeoutMs 毫秒）。
	/// **仅供自检/诊断使用** —— 会阻塞当前线程，不要在 UI 线程或锁内调用。
	/// </summary>
	public static bool WaitForState(int state, int timeoutMs)
	{
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < timeoutMs)
		{
			if (GetState() == state) return true;
			Thread.Sleep(50);
		}
		return GetState() == state;
	}

	// ================================================================
	//  展示
	// ================================================================

	/// <summary>用于日志/界面展示的文本。</summary>
	public static string Describe(int state)
	{
		switch (state)
		{
			case STATE_ON: return "开";
			case STATE_OFF: return "关";
			default: return "读取失败";
		}
	}

	private static string DescribeNtStatus(uint status)
	{
		switch (status)
		{
			case 0xC000000D: return "STATUS_INVALID_PARAMETER";
			case 0xC0000022: return "STATUS_ACCESS_DENIED";
			case 0xC0000034: return "STATUS_OBJECT_NAME_NOT_FOUND";
			case 0xC0000061: return "STATUS_PRIVILEGE_NOT_HELD";
			case 0xC0000008: return "STATUS_INVALID_HANDLE";
			case 0xC0000024: return "STATUS_TYPE_MISMATCH";
			case 0xC00000BB: return "STATUS_NOT_SUPPORTED";
			default: return "未知";
		}
	}
}
