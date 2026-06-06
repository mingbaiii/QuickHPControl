using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HP.SystemControl.BiosWmi;

namespace Payload
{
    public static class BiosWmiManager
    {
        // BIOS 位 → SystemControlMode 枚举值 (来自 HP 官方 modeOrderMap)
        // Performance=0, Balanced=1, Cool=2, Quiet=3, PowerSaver=4, SmartSense=5, Silent=6
        private static readonly Dictionary<int, int> BitToMode = new Dictionary<int, int>
        {
            { 1, 5 }, { 2, 1 }, { 3, 0 }, { 4, 2 }, { 5, 3 }, { 6, 4 }, { 7, 6 }
        };

        public static Action<string> LogCallback { get; set; }

        private static void Log(string msg) { LogCallback?.Invoke(msg); }

        /// <summary>
        /// 直接调用 WmiFunctionHelper（运行在可信进程中，不再需要 WMI 回退）
        /// </summary>
        public static int GetCurrentMode()
        {
            try
            {
                Log("GetThermalControlMode() 调用...");
                int mode = WmiFunctionHelper.GetThermalControlMode();
                Log("GetThermalControlMode 返回: " + mode);
                return mode;
            }
            catch (Exception ex)
            {
                Log("GetThermalControlMode 异常: " + ex.GetType().Name + ": " + ex.Message);
                var inner = ex.InnerException;
                int depth = 1;
                while (inner != null)
                {
                    Log("  内部[" + depth + "]: " + inner.GetType().Name + ": " + inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
                return -1;
            }
        }

        public static void SetMode(int mode)
        {
            Log("SetThermalControlMode(" + mode + ")...");

            // 写入前先读 BIOS 当前值
            int beforeMode = GetCurrentMode();

            WmiFunctionHelper.SetThermalControlMode(mode);
            Log("SetThermalControlMode 完成");

            // 立即读回验证（无 Sleep，避免给 HP 后台进程留下检测窗口）
            int afterMode = GetCurrentMode();
        }

        public static async Task<byte[]> ReadBiosData(int retries = 3)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Log("BiosWmiCmd_Get 第 " + (i + 1) + " 次尝试...");
                    byte[] data = await WmiFunctionHelper.BiosWmiCmd_Get(1, 13, null, 0, 128);
                    Log("BiosWmiCmd_Get 返回: " + (data == null ? "null" : "byte[" + data.Length + "]"));
                    if (data != null && data.Length == 128)
                    {
                        Log("  data[6]=0x" + data[6].ToString("X2") + " data[7]=0x" + data[7].ToString("X2") + " data[8]=0x" + data[8].ToString("X2"));
                        return data;
                    }
                }
                catch (Exception ex)
                {
                    Log("BiosWmiCmd_Get 异常: " + ex.GetType().Name + ": " + ex.Message);
                    var inner = ex.InnerException;
                    int depth = 1;
                    while (inner != null)
                    {
                        Log("  内部[" + depth + "]: " + inner.GetType().Name + ": " + inner.Message);
                        inner = inner.InnerException;
                        depth++;
                    }
                }

                if (i < retries - 1)
                {
                    Log("等待 1 秒后重试...");
                    await Task.Delay(1000);
                }
            }

            Log("HP DLL 方式全部失败，返回 null");
            return null;
        }

        public static async Task<List<int>> GetSupportedModes()
        {
            var modes = new List<int>();
            byte[] data = await ReadBiosData();
            if (data == null)
            {
                Log("无法获取支持的模式列表（数据为空）");
                return modes;
            }

            byte mask = data[6];
            if ((mask & 1) == 0)
            {
                Log("模式掩码无效: data[6]=0x" + mask.ToString("X2") + " (bit0=0)");
                return modes;
            }

            foreach (var kv in BitToMode)
            {
                bool bitSet = (mask & (1 << kv.Key)) != 0;
                if (bitSet)
                    modes.Add(kv.Value);
            }
            Log("解析出 " + modes.Count + " 个支持的模式");
            return modes;
        }

        public static async Task<int> GetVersion()
        {
            byte[] data = await ReadBiosData();
            if (data == null)
            {
                Log("无法获取版本信息（数据为空）");
                return 0;
            }
            int version = (data[8] & 1) == 0 ? 1 : 2;
            Log("版本: data[8]=0x" + data[8].ToString("X2") + " → V" + version);
            return version;
        }

        public static async Task<bool> IsSupportSmartSense()
        {
            byte[] data = await ReadBiosData();
            if (data == null) return false;
            bool supported = (data[6] & 2) != 0;
            Log("SmartSense: data[6]=0x" + data[6].ToString("X2") + " bit1=" + supported);
            return supported;
        }
    }
}
