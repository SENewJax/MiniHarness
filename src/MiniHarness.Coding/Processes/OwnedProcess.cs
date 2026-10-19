using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MiniHarness.Coding.Processes
{
    // The child cannot execute or spawn before job ownership is established.
    // STARTUPINFOEX restricts inherited handles to this session's three pipes.
    internal sealed class OwnedProcess : IDisposable
    {
        public Process Process { get; private set; } = null!;
        public ProcessJob Job { get; private set; } = null!;
        public StreamReader Output { get; private set; } = null!;
        public StreamReader Error { get; private set; } = null!;
        public StreamWriter Input { get; private set; } = null!;
        private SafeFileHandle? thread;
        private SafeFileHandle? nativeProcess;
        public static OwnedProcess Create(string executable, string[] arguments, string workingDirectory, Action? beforeJobBind = null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned background process trees require Windows Job support.");
            var owned = new OwnedProcess();
            var handles = new System.Collections.Generic.List<SafeFileHandle>();
            IntPtr attributes = IntPtr.Zero, list = IntPtr.Zero;
            bool initialized = false;
            PROCESS_INFORMATION info = default;
            try
            {
                var security = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), Inherit = true };
                void Pipe(out SafeFileHandle read, out SafeFileHandle write)
                {
                    if (!CreatePipe(out read, out write, ref security, 0)) throw new Win32Exception();
                    handles.Add(read); handles.Add(write);
                }
                Pipe(out var stdinRead, out var stdinWrite); Pipe(out var stdoutRead, out var stdoutWrite); Pipe(out var stderrRead, out var stderrWrite);
                foreach (var parent in new[] { stdinWrite, stdoutRead, stderrRead })
                    if (!SetHandleInformation(parent, 1, 0)) throw new Win32Exception();
                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(bytes);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception();
                initialized = true;
                list = Marshal.AllocHGlobal(IntPtr.Size * 3);
                var inherited = new[] { stdinRead, stdoutWrite, stderrWrite };
                for (int i = 0; i < 3; i++) Marshal.WriteIntPtr(list, i * IntPtr.Size, inherited[i].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, list, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                var startup = new STARTUPINFOEX { Attributes = attributes };
                startup.Startup.Size = Marshal.SizeOf<STARTUPINFOEX>(); startup.Startup.Flags = 0x100;
                startup.Startup.Input = stdinRead.DangerousGetHandle(); startup.Startup.Output = stdoutWrite.DangerousGetHandle(); startup.Startup.Error = stderrWrite.DangerousGetHandle();
                var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero, workingDirectory, ref startup, out info))
                    throw new Win32Exception();
                owned.nativeProcess = new SafeFileHandle(info.Process, true);
                owned.thread = new SafeFileHandle(info.Thread, true);
                owned.Process = Process.GetProcessById((int)info.ProcessId);
                beforeJobBind?.Invoke();
                owned.Job = ProcessJob.TryCreateAndAssign(owned.Process) ?? throw new InvalidOperationException("Cannot establish suspended process job ownership.");
                owned.Output = new StreamReader(new FileStream(stdoutRead, FileAccess.Read), Encoding.UTF8);
                owned.Error = new StreamReader(new FileStream(stderrRead, FileAccess.Read), Encoding.UTF8);
                owned.Input = new StreamWriter(new FileStream(stdinWrite, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
                handles.Remove(stdoutRead); handles.Remove(stderrRead); handles.Remove(stdinWrite);
                return owned;
            }
            catch (Exception error)
            {
                bool confirmed = info.Process == IntPtr.Zero;
                if (owned.nativeProcess != null)
                    try { TerminateProcess(owned.nativeProcess, 1); confirmed = WaitForSingleObject(owned.nativeProcess, 2000) == 0; } catch { }
                owned.Dispose(); throw new LaunchException(error, confirmed, info.ProcessId == 0 ? null : (int)info.ProcessId);
            }
            finally
            {
                foreach (var handle in handles) handle.Dispose();
                if (attributes != IntPtr.Zero) { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            }
        }
        public void Resume()
        {
            if (thread == null || ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            thread.Dispose(); thread = null;
        }
        private static string Quote(string argument)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char value in argument)
            {
                if (value == '\\') { slashes++; continue; }
                result.Append('\\', value == '"' ? slashes * 2 + 1 : slashes); result.Append(value); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        public void Dispose()
        {
            Job?.Dispose(); thread?.Dispose(); nativeProcess?.Dispose(); Input?.Dispose(); Output?.Dispose(); Error?.Dispose(); Process?.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
        {
            public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            public short Show, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES security, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr bytes, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        private static extern bool CreateProcess(string? app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    }
    internal sealed class LaunchException : Exception
    {
        public bool TerminationConfirmed { get; }
        public int? Pid { get; }
        public LaunchException(Exception error, bool confirmed, int? pid) : base(error.Message, error) { TerminationConfirmed = confirmed; Pid = pid; }
    }
}
