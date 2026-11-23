using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Test windows belong to a private desktop from process creation.
// SwitchDesktop is never called. No keyboard input is injected into the session.
internal sealed class PrivateDesktop : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int Pid, Tid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, int access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int byteCount, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inherit, int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    internal Process Start(string exe, string arguments)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = name };
        var command = new StringBuilder("\"" + exe + "\" " + arguments);
        ProcessInfo child;
        if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref startup, out child))
            throw new Win32Exception();
        try {
            var process = Process.GetProcessById(child.Pid);
            var handle = process.Handle; // Keep a handle with query rights after the child exits.
            return process;
        }
        finally { CloseHandle(child.Thread); CloseHandle(child.Process); }
    }

    private readonly string name = "RaskladkaTest" + Guid.NewGuid().ToString("N");
    private readonly IntPtr handle;

    internal PrivateDesktop()
    {
        handle = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new Win32Exception();
    }

    internal static void RequireTestDesktop()
    {
        var name = new StringBuilder(128);
        int bytes;
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out bytes) ||
            !name.ToString().StartsWith("RaskladkaTest", StringComparison.Ordinal))
            throw new InvalidOperationException("The test worker requires its private desktop.");
    }

    public void Dispose() { CloseDesktop(handle); }
}
