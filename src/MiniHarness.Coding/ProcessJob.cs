using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MiniHarness.Coding
{
    // Windows Job Object wrapper used to reclaim a whole launcher tree.
    //
    // Why a job is required: Process.Kill(entireProcessTree: true) can only walk
    // a tree whose root is still alive. A launcher that spawns a child and exits
    // immediately orphans that child; if the child inherited our redirected
    // stdout/stderr handles it keeps the pipes open and ProcessRunner used to
    // wait for EOF forever (while still reporting Exited/0). A child launched
    // without CREATE_BREAKAWAY_FROM_JOB is assigned to the parent's job
    // automatically, so the job keeps owning it AFTER the launcher is gone.
    // TerminateJobObject kills every member and the job's PID list lets us
    // confirm the tree is actually empty instead of assuming.
    //
    // KILL_ON_JOB_CLOSE is a last-resort safety net: if our own process dies
    // while a tree is still running, closing the last job handle reaps it.
    internal sealed class ProcessJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const int JobObjectBasicProcessIdList = 3;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private readonly SafeFileHandle handle;

        private ProcessJob(SafeFileHandle handle) { this.handle = handle; }

        public bool IsValid => handle != null && !handle.IsInvalid && !handle.IsClosed;

        // Returns null on non-Windows hosts or when assignment fails; callers
        // then degrade to a bounded, conservative outcome (never an optimistic
        // Exited/0 for an unconfirmable tree).
        public static ProcessJob? TryCreateAndAssign(System.Diagnostics.Process process)
        {
            if (!OperatingSystem.IsWindows()) return null;
            SafeFileHandle? job = null;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == null || job.IsInvalid) return null;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr raw = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, raw, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, raw, (uint)length))
                        return null;
                }
                finally { Marshal.FreeHGlobal(raw); }

                if (!AssignProcessToJobObject(job, process.SafeHandle.DangerousGetHandle()))
                    return null;

                var attached = new ProcessJob(job);
                job = null; // ownership transferred
                return attached;
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or ObjectDisposedException)
            {
                return null;
            }
            finally { job?.Dispose(); }
        }

        // Kill every process still in the job (orphans included) and wait until
        // the membership list is provably empty. Returns false when termination
        // could not be issued or emptiness cannot be confirmed.
        public async Task<bool> TerminateAndWaitEmptyAsync(TimeSpan timeout)
        {
            if (!IsValid) return false;
            try
            {
                if (!TerminateJobObject(handle, 1)) return false;
            }
            catch (Win32Exception) { return false; }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                int count = CountProcesses();
                if (count == 0) return true;
                if (count < 0) return false; // query failed: cannot confirm
                await Task.Delay(100).ConfigureAwait(false);
            }
            return CountProcesses() == 0;
        }

        // Number of live processes currently in the job, or -1 when it cannot be
        // queried. The list is variable-length; a generous fixed buffer suffices
        // for a build/test process tree and is refilled by the OS on each call.
        internal int CountProcesses()
        {
            if (!IsValid) return -1;
            const int capacity = 1024;
            int bytes = 8 + IntPtr.Size * capacity;
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                Marshal.WriteInt32(buffer, 0, capacity);
                if (!QueryInformationJobObject(handle, JobObjectBasicProcessIdList, buffer, (uint)bytes, out _))
                    return -1;
                return Marshal.ReadInt32(buffer, 4);
            }
            catch (Win32Exception) { return -1; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            try { handle?.Dispose(); } catch (ObjectDisposedException) { }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle? CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryInformationJobObject(SafeFileHandle hJob, int infoClass,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength, out uint lpReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
