using System;
using System.Runtime.InteropServices;

namespace Payload
{
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

        public static bool SetForMode(int mode, bool isOnBattery)
        {
            Guid target;
            switch (mode)
            {
                case 0: target = BEST_PERFORMANCE; break;
                case 4: target = BEST_POWER_EFFICIENCY; break;  // PowerSaver=4
                default: target = BALANCED; break;
            }

            Guid cur;
            if (GetCurrent(isOnBattery, out cur) && cur == target)
                return true;

            return Set(isOnBattery, target);
        }

        // PowerSaver=4, SmartSense=5 (V2 默认平衡回退)
        public static int GetModeFromGuid(Guid guid, int balancedFallback = 5)
        {
            if (guid == BEST_PERFORMANCE) return 0;   // Performance
            if (guid == BEST_POWER_EFFICIENCY) return 4;  // PowerSaver
            if (guid == BALANCED) return balancedFallback;  // SmartSense/Balanced/Cool/Quiet/Silent
            return balancedFallback;
        }

        /// <summary>
        /// V2 模式下电源计划只区分三大类:
        ///   返回 0=Performance, 4=PowerSaver, -1=Balanced系列(需从BIOS读取子模式)
        /// </summary>
        public static int GetCategoryFromGuid(Guid guid)
        {
            if (guid == BEST_PERFORMANCE) return 0;
            if (guid == BEST_POWER_EFFICIENCY) return 4;
            if (guid == BALANCED) return -1;
            return -1;  // 未知 guid → 回退到 BIOS
        }
    }
}
