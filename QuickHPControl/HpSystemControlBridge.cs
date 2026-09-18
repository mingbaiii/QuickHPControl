using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickHPControl;

/// <summary>
/// 让 HP 的后台服务「以为」模式是用户自己切的 —— 直接改写 HP 自己的设置存储。
///
/// ────────────────────────────────────────────────────────────────
/// 为什么需要它（问题现象）
/// ────────────────────────────────────────────────────────────────
/// 只写 BIOS 时，切到「最冷 / 平衡 / 安静 / 静音」会在几秒内跳回「智能模式」。
/// 原因是 HP.SystemControl.Background.exe 一直在后台跑，它会：
///   1. 监听 Windows 电源模式（overlay）变化；
///   2. 一旦发现电源模式变成「平衡」而 triggerSource == Other，
///      就调用 BidirectionalSystemControl.ConvertToSystemControl(Balanced)，
///      把 SelectedBalancedSerices 强行改回 _defaultOsBalancedMode（= SmartSense 智能模式）；
///   3. 再把智能模式写回 BIOS —— 于是用户看到的模式被「抢」走了。
///
/// 老版（注入方案）的 PerformanceController.StoreModeToAppData() 之所以能压住它，
/// 是因为代码跑在 HP 进程里、直接写 HP 的 ApplicationData。新版不注入，
/// 就必须从外部写同一个存储。
///
/// ────────────────────────────────────────────────────────────────
/// 存储位置与格式（反汇编 + 实测取证，见 hp-bios/README.md 第 14 节）
/// ────────────────────────────────────────────────────────────────
/// 文件  %LOCALAPPDATA%\Packages\AD2F1837.myHP_v10z8vjag6ke6\Settings\settings.dat
///       —— 这是 UWP ApplicationData 的 LocalSettings 注册表 hive。
///
/// 键    LocalState\SystemControlAppDataHelper   → 值 StorageInfo
///       LocalState\EffectivePowerModeAppDataHelper → 值 StorageInfo
///       LocalState\MessageFromProcessAppDataHelper → 值 StorageInfo
///
/// 值类型 0x05F5E10C（UWP 设置存储专用类型），数据布局：
///       UTF-16LE 的 JSON 文本 + 0x0000 结束符 + 8 字节 FILETIME
///
/// JSON 结构（Newtonsoft 序列化的扁平对象，字段顺序固定）：
///   SystemControlAppData
///     {"LastSelectedNonPowerSaverMode":5,"SelectedBalancedSerices":5,
///      "DcIsPowerSavingModeBefore":false,"IsFocusModeOn":false,"FocusModePid":0,
///      "Version":2,"PRDontShowAgainTime":"0001-01-01T00:00:00"}
///   EffectivePowerModeAppData
///     {"triggerSource":-1}
///   MessageFromProcessAppData
///     {"DisplayModeOnUI":4,"IsEnergySaverMode":false,
///      "IsPromotedToPerformanceMode":false,"IsAcMode":true}
///
/// 枚举（从 HP.SystemControl.AppData.dll 的元数据 Constant 表里读出来的真值）：
///   SystemControlMode              Performance=0 Balanced=1 Cool=2 Quiet=3
///                                  PowerSaver=4 SmartSense=5 Silent=6
///   EffectivePowerModeTriggerSource  Other=-1  UserAction=0
///   ↑ 出厂默认是 -1 = Other，所以 HP 默认就会「抢」模式；必须改成 0 = UserAction。
///
/// ────────────────────────────────────────────────────────────────
/// 关键实现细节
/// ────────────────────────────────────────────────────────────────
/// · 用 RegLoadAppKeyW(dwOptions = 0) 打开。此时 HP 进程已把该 hive 打开着，
///   注册表会**复用同一个已加载的 hive 对象**（因此写入立刻对 HP 可见）。
///   注意不能用 REG_PROCESS_APPKEY —— 那会强制私有加载，直接报
///   ERROR_SHARING_VIOLATION(32)。
/// · 全程 best-effort：HP 没装、文件不存在、权限不足等情况一律静默跳过，
///   绝不因为「写不了 HP 的状态」而影响主流程（BIOS 模式已经生效了）。
/// · 所有调用都在调用方后台线程执行，本类自身不做任何等待/重试。
/// </summary>
public static class HpSystemControlBridge
{
	// ── hive 值类型 ──
	/// <summary>UWP 设置存储里字符串值的注册表类型（非标准 REG_SZ）。</summary>
	private const uint REGTYPE_UWP_STRING = 0x05F5E10C;

	// ── 键路径 ──
	private const string KEY_SYSTEM_CONTROL = @"LocalState\SystemControlAppDataHelper";
	private const string KEY_EFFECTIVE_POWER = @"LocalState\EffectivePowerModeAppDataHelper";
	private const string KEY_MESSAGE_FROM_PROCESS = @"LocalState\MessageFromProcessAppDataHelper";
	private const string VALUE_STORAGE_INFO = "StorageInfo";

	// ── JSON 字段名 ──
	private const string F_SELECTED_BALANCED = "SelectedBalancedSerices";
	private const string F_LAST_NON_POWER_SAVER = "LastSelectedNonPowerSaverMode";
	private const string F_TRIGGER_SOURCE = "triggerSource";
	private const string F_IS_ENERGY_SAVER = "IsEnergySaverMode";

	// ── 枚举值 ──
	/// <summary>EffectivePowerModeTriggerSource.UserAction —— 声明「这是用户主动切的」。</summary>
	private const int TRIGGER_USER_ACTION = 0;

	/// <summary>SystemControlMode.SmartSense —— HP 的平衡系列默认值。</summary>
	private const int MODE_SMART_SENSE = 5;

	/// <summary>SystemControlMode.PowerSaver。</summary>
	private const int MODE_POWER_SAVER = 4;

	/// <summary>属于「平衡系列」的模式：HP 只用 SelectedBalancedSerices 记这一个值。</summary>
	private static bool IsBalancedSeries(int mode)
	{
		return mode == 5 || mode == 1 || mode == 2 || mode == 3 || mode == 6;
	}

	// ================================================================
	//  状态
	// ================================================================

	private static readonly object _lock = new object();
	private static bool _probed;
	private static string _hivePath;      // null = 本机没有 HP 设置存储
	private static string _lastError;

	/// <summary>上一次操作的错误信息（成功或未启用时为 null）。</summary>
	public static string GetLastError()
	{
		lock (_lock) { return _lastError; }
	}

	/// <summary>本机是否存在可写的 HP 设置存储（首次调用会做一次探测并缓存）。</summary>
	public static bool IsAvailable
	{
		get
		{
			lock (_lock)
			{
				EnsureProbed();
				return _hivePath != null;
			}
		}
	}

	/// <summary>已解析到的 settings.dat 完整路径（不可用时为 null）。</summary>
	public static string HivePath
	{
		get
		{
			lock (_lock)
			{
				EnsureProbed();
				return _hivePath;
			}
		}
	}

	// ================================================================
	//  对外接口
	// ================================================================

	/// <summary>
	/// 把「用户切到了 mode」这件事写进 HP 的存储。
	///
	/// 写两处：
	///   SystemControlAppData.SelectedBalancedSerices = mode（平衡系列模式才写）
	///   EffectivePowerModeAppData.triggerSource      = UserAction
	///
	/// 必须在改电源模式**之前**调用 —— 否则 HP 会先看到电源模式变化、
	/// 按 triggerSource == Other 把模式抢回智能模式。
	/// </summary>
	/// <returns>true = 已写入；false = 本机不适用或写入失败（不影响主流程）。</returns>
	public static bool PublishUserMode(int mode)
	{
		lock (_lock)
		{
			EnsureProbed();
			if (_hivePath == null)
			{
				_lastError = null;
				return false;
			}

			bool ok = false;
			string err = null;

			// 1) SelectedBalancedSerices / LastSelectedNonPowerSaverMode
			try
			{
				UpdateJsonValue(_hivePath, KEY_SYSTEM_CONTROL, VALUE_STORAGE_INFO, json =>
				{
					if (IsBalancedSeries(mode))
					{
						json.SetInt(F_SELECTED_BALANCED, mode);
					}
					else if (mode != MODE_POWER_SAVER)
					{
						// 性能模式：HP 只记「上一个非省电模式」，平衡系列值保持不动
						json.SetInt(F_LAST_NON_POWER_SAVER, mode);
					}
					else
					{
						// 省电模式：把当前平衡系列值留作「回到平衡时用哪个」
						json.SetInt(F_LAST_NON_POWER_SAVER, json.GetInt(F_SELECTED_BALANCED, MODE_SMART_SENSE));
					}
					return true;
				});
				ok = true;
			}
			catch (Exception ex)
			{
				err = "SystemControlAppData: " + ex.Message;
			}

			// 2) triggerSource = UserAction（★ 阻止 HP 抢模式的关键一步）
			try
			{
				UpdateJsonValue(_hivePath, KEY_EFFECTIVE_POWER, VALUE_STORAGE_INFO, json =>
				{
					json.SetInt(F_TRIGGER_SOURCE, TRIGGER_USER_ACTION);
					return true;
				});
				ok = true;
			}
			catch (Exception ex)
			{
				err = (err == null ? "" : err + " | ") + "EffectivePowerModeAppData: " + ex.Message;
			}

			_lastError = err;
			return ok;
		}
	}

	/// <summary>
	/// 同步「Windows 节能模式」状态到 HP 的 IPC 记录（MessageFromProcessAppData.IsEnergySaverMode）。
	/// 纯粹是为了让 HP 的界面/内部状态和我们一致，避免它按旧值做判断。
	/// </summary>
	public static bool PublishEnergySaver(bool on)
	{
		lock (_lock)
		{
			EnsureProbed();
			if (_hivePath == null)
			{
				_lastError = null;
				return false;
			}

			try
			{
				UpdateJsonValue(_hivePath, KEY_MESSAGE_FROM_PROCESS, VALUE_STORAGE_INFO, json =>
				{
					json.SetBool(F_IS_ENERGY_SAVER, on);
					return true;
				});
				_lastError = null;
				return true;
			}
			catch (Exception ex)
			{
				_lastError = "MessageFromProcessAppData: " + ex.Message;
				return false;
			}
		}
	}

	/// <summary>读 HP 记的平衡系列模式（诊断用；读不到返回 -1）。</summary>
	public static int ReadStoredBalancedMode()
	{
		lock (_lock)
		{
			EnsureProbed();
			if (_hivePath == null) return -1;

			try
			{
				string json = ReadValue(_hivePath, KEY_SYSTEM_CONTROL, VALUE_STORAGE_INFO);
				if (json == null) return -1;
				return FlatJson.Parse(json).GetInt(F_SELECTED_BALANCED, -1);
			}
			catch
			{
				return -1;
			}
		}
	}

	/// <summary>读 HP 记的 triggerSource（诊断用；读不到返回 int.MinValue）。</summary>
	public static int ReadTriggerSource()
	{
		lock (_lock)
		{
			EnsureProbed();
			if (_hivePath == null) return int.MinValue;

			try
			{
				string json = ReadValue(_hivePath, KEY_EFFECTIVE_POWER, VALUE_STORAGE_INFO);
				if (json == null) return int.MinValue;
				return FlatJson.Parse(json).GetInt(F_TRIGGER_SOURCE, int.MinValue);
			}
			catch
			{
				return int.MinValue;
			}
		}
	}

	// ================================================================
	//  探测 settings.dat
	// ================================================================

	private static void EnsureProbed()
	{
		if (_probed) return;
		_probed = true;

		try
		{
			string packages = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Packages");

			if (!Directory.Exists(packages)) return;

			// 先按已知包家族名直接找（正常路径，最快）
			string[] known =
			{
				"AD2F1837.myHP_v10z8vjag6ke6",
				"AD2F1837.OMENCommandCenter_v10z8vjag6ke6"
			};

			foreach (string pfn in known)
			{
				string p = Path.Combine(packages, pfn, "Settings", "settings.dat");
				if (File.Exists(p))
				{
					_hivePath = p;
					return;
				}
			}

			// 兜底：扫一遍 AD2F1837.* 包目录（HP 的 publisher id 固定）
			string best = null;
			foreach (string dir in Directory.GetDirectories(packages, "AD2F1837.*"))
			{
				string p = Path.Combine(dir, "Settings", "settings.dat");
				if (!File.Exists(p)) continue;

				string name = Path.GetFileName(dir);
				if (name.IndexOf("myHP", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					_hivePath = p;
					return;
				}
				if (best == null) best = p;
			}

			_hivePath = best;
		}
		catch
		{
			_hivePath = null;
		}
	}

	// ================================================================
	//  hive 读写
	// ================================================================

	/// <summary>读一个 UWP 设置值，去掉 UTF-16 结束符与尾部 FILETIME，返回 JSON 文本。</summary>
	private static string ReadValue(string hivePath, string subKey, string valueName)
	{
		IntPtr hive = IntPtr.Zero;
		IntPtr key = IntPtr.Zero;
		try
		{
			int rc = RegLoadAppKeyW(hivePath, out hive, KEY_READ | KEY_WRITE, 0, 0);
			if (rc != 0 || hive == IntPtr.Zero) return null;

			rc = RegOpenKeyExW(hive, subKey, 0, KEY_READ | KEY_WRITE, out key);
			if (rc != 0 || key == IntPtr.Zero) return null;

			uint type = 0;
			uint size = 0;
			rc = RegQueryValueExW(key, valueName, IntPtr.Zero, ref type, null, ref size);
			if (rc != 0 || size < 2) return null;

			byte[] buf = new byte[size];
			rc = RegQueryValueExW(key, valueName, IntPtr.Zero, ref type, buf, ref size);
			if (rc != 0) return null;

			return DecodeValue(buf, (int)size);
		}
		finally
		{
			if (key != IntPtr.Zero) RegCloseKey(key);
			if (hive != IntPtr.Zero) RegCloseKey(hive);
		}
	}

	/// <summary>
	/// 读 → 改 → 写回一个 UWP 设置值，保留原有类型与数据布局。
	/// mutate 返回 false 表示不需要写。
	/// </summary>
	private static void UpdateJsonValue(string hivePath, string subKey, string valueName, Func<FlatJson, bool> mutate)
	{
		IntPtr hive = IntPtr.Zero;
		IntPtr key = IntPtr.Zero;
		try
		{
			int rc = RegLoadAppKeyW(hivePath, out hive, KEY_READ | KEY_WRITE, 0, 0);
			if (rc != 0 || hive == IntPtr.Zero)
			{
				throw new InvalidOperationException("RegLoadAppKey 失败 rc=" + rc);
			}

			// 子键必须已存在（HP 建的），不存在说明布局变了，直接放弃
			rc = RegOpenKeyExW(hive, subKey, 0, KEY_READ | KEY_WRITE, out key);
			if (rc != 0 || key == IntPtr.Zero)
			{
				throw new InvalidOperationException("找不到子键 " + subKey + " (rc=" + rc + ")");
			}

			uint type = REGTYPE_UWP_STRING;
			uint size = 0;
			byte[] buf = null;

			rc = RegQueryValueExW(key, valueName, IntPtr.Zero, ref type, null, ref size);
			if (rc == 0 && size >= 2)
			{
				buf = new byte[size];
				rc = RegQueryValueExW(key, valueName, IntPtr.Zero, ref type, buf, ref size);
				if (rc != 0) throw new InvalidOperationException("读取 " + valueName + " 失败 rc=" + rc);
			}
			else
			{
				// 值不存在：按空对象起头，类型沿用 UWP 字符串类型
				type = REGTYPE_UWP_STRING;
				buf = null;
			}

			string jsonText = buf == null ? "{}" : DecodeValue(buf, (int)size);
			FlatJson json = FlatJson.Parse(jsonText);

			if (!mutate(json)) return;

			byte[] data = EncodeValue(json.ToString());
			rc = RegSetValueExW(key, valueName, 0, type, data, (uint)data.Length);
			if (rc != 0) throw new InvalidOperationException("写入 " + valueName + " 失败 rc=" + rc);
		}
		finally
		{
			if (key != IntPtr.Zero) RegCloseKey(key);
			if (hive != IntPtr.Zero) RegCloseKey(hive);
		}
	}

	/// <summary>UTF-16 JSON + 0x0000 结束符 + 8 字节 FILETIME（当前时间）。</summary>
	private static byte[] EncodeValue(string json)
	{
		byte[] text = Encoding.Unicode.GetBytes(json);
		byte[] data = new byte[text.Length + 2 + 8];

		Buffer.BlockCopy(text, 0, data, 0, text.Length);
		// text.Length 起 2 字节已经是 0（UTF-16 结束符）
		long ft = DateTime.UtcNow.ToFileTimeUtc();
		for (int i = 0; i < 8; i++)
		{
			data[text.Length + 2 + i] = (byte)(ft >> (8 * i));
		}
		return data;
	}

	/// <summary>从原始值数据里取出 JSON 文本（截到第一个 UTF-16 结束符）。</summary>
	private static string DecodeValue(byte[] buf, int len)
	{
		int usable = len;
		for (int i = 0; i + 1 < len; i += 2)
		{
			if (buf[i] == 0 && buf[i + 1] == 0)
			{
				usable = i;
				break;
			}
		}
		if (usable <= 0) return "{}";
		return Encoding.Unicode.GetString(buf, 0, usable);
	}

	// ================================================================
	//  极简扁平 JSON（只处理 {"k":标量,...}，保留字段顺序与未知字段）
	// ================================================================

	private sealed class FlatJson
	{
		private readonly List<string> _keys = new List<string>();
		private readonly List<string> _rawValues = new List<string>();

		public static FlatJson Parse(string text)
		{
			var o = new FlatJson();
			if (string.IsNullOrEmpty(text)) return o;

			int i = 0;
			int n = text.Length;

			// 跳到第一个 '{'
			while (i < n && text[i] != '{') i++;
			if (i >= n) return o;
			i++;

			while (i < n)
			{
				while (i < n && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
				if (i >= n || text[i] == '}') break;

				// 键
				if (text[i] != '"') break;
				int keyStart = ++i;
				while (i < n && text[i] != '"')
				{
					if (text[i] == '\\') i++;
					i++;
				}
				string key = text.Substring(keyStart, Math.Min(i, n) - keyStart);
				i++;

				while (i < n && (char.IsWhiteSpace(text[i]) || text[i] == ':')) i++;
				if (i >= n) break;

				// 值（标量：数字 / true / false / 字符串 / null）
				int valStart = i;
				if (text[i] == '"')
				{
					i++;
					while (i < n && text[i] != '"')
					{
						if (text[i] == '\\') i++;
						i++;
					}
					i++;
				}
				else
				{
					while (i < n && text[i] != ',' && text[i] != '}') i++;
				}
				string val = text.Substring(valStart, i - valStart).Trim();

				o._keys.Add(key);
				o._rawValues.Add(val);
			}
			return o;
		}

		public int GetInt(string key, int fallback)
		{
			int idx = _keys.IndexOf(key);
			if (idx < 0) return fallback;
			return int.TryParse(_rawValues[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
				? v : fallback;
		}

		public void SetInt(string key, int value)
		{
			SetRaw(key, value.ToString(CultureInfo.InvariantCulture));
		}

		public void SetBool(string key, bool value)
		{
			SetRaw(key, value ? "true" : "false");
		}

		private void SetRaw(string key, string raw)
		{
			int idx = _keys.IndexOf(key);
			if (idx >= 0)
			{
				_rawValues[idx] = raw;
				return;
			}
			_keys.Add(key);
			_rawValues.Add(raw);
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.Append('{');
			for (int i = 0; i < _keys.Count; i++)
			{
				if (i > 0) sb.Append(',');
				sb.Append('"').Append(_keys[i]).Append("\":").Append(_rawValues[i]);
			}
			sb.Append('}');
			return sb.ToString();
		}
	}

	// ================================================================
	//  P/Invoke
	// ================================================================

	private const int KEY_READ = 0x20019;
	private const int KEY_WRITE = 0x20006;

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int RegLoadAppKeyW(
		string lpFile, out IntPtr phkResult, int samDesired, int dwOptions, int reserved);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern int RegCloseKey(IntPtr hKey);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int RegOpenKeyExW(
		IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int RegQueryValueExW(
		IntPtr hKey, string lpValueName, IntPtr lpReserved,
		ref uint lpType, byte[] lpData, ref uint lpcbData);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int RegSetValueExW(
		IntPtr hKey, string lpValueName, int reserved,
		uint dwType, byte[] lpData, uint cbData);
}
