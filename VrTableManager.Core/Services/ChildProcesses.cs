using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VrTableManager.Services;

/// <summary>
/// Helper processes that must not outlive the one that started them (attract music recordings,
/// <see cref="RomAudioBatch"/>). Each is put in a Windows job object that ends its processes when this process exits,
/// however it exits: closing the manager mid-batch, or the VR lobby quitting while it records. Elsewhere, or if Windows
/// refuses, nothing happens and the caller's own kill on cancel is all there is.
/// </summary>
public static class ChildProcesses
{
    static readonly Lazy<IntPtr> Job = new(CreateJob);

    /// <summary>Ties <paramref name="p"/> to this process's lifetime. False when it couldn't be.</summary>
    public static bool Adopt(Process p)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var job = Job.Value;
            return job != IntPtr.Zero && AssignProcessToJobObject(job, p.Handle);
        }
        catch
        {
            return false; // already exited, or not allowed
        }
    }

    static IntPtr CreateJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (
                    !SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        ptr,
                        (uint)size
                    )
                )
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            // Never closed: the handle closing as this process exits is what ends the children.
            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
