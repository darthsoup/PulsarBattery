using System;
using System.Runtime.InteropServices;

namespace PulsarBattery.Tools;

/// <summary>
/// Toggles Windows EcoQoS while keeping HID reads and battery alarms at normal scheduling priority.
/// </summary>
internal static class EfficiencyMode
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint NORMAL_PRIORITY_CLASS = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, uint infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint priorityClass);

    public static void Set(bool enabled)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };

            var process = GetCurrentProcess();
            SetProcessInformation(process, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            // Idle priority can postpone lock-screen battery checks behind other processes.
            SetPriorityClass(process, NORMAL_PRIORITY_CLASS);
        }
        catch
        {
        }
    }
}
