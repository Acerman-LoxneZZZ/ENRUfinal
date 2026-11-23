using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Raskladka.Integration
{
    internal struct EditSpan
    {
        internal readonly int Start, End;
        internal EditSpan(int start, int end) { Start = start; End = end; }
        internal bool Same(EditSpan other) { return Start == other.Start && End == other.End; }
        internal bool Empty { get { return Start == End; } }
    }

    internal sealed class NativeField
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadInfo
        {
            internal int Size, Flags;
            internal IntPtr Active, Focus, Capture, Menu, MoveSize, Caret;
            internal int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int process);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref ThreadInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr Send(IntPtr hwnd, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr ReadSelection(IntPtr hwnd, uint message, out int start, out int end, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr Replace(IntPtr hwnd, uint message, IntPtr w, string text, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
        private const uint Flags = 0x23; // SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT

        internal readonly IntPtr Window, Control;
        internal readonly string Class, ProcessName;
        private NativeField(IntPtr window, IntPtr control, string name, string processName)
        {
            Window = window; Control = control; Class = name; ProcessName = processName;
        }

        internal static NativeField Capture()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            int pid;
            uint thread = GetWindowThreadProcessId(window, out pid);
            ThreadInfo info = new ThreadInfo { Size = Marshal.SizeOf(typeof(ThreadInfo)) };
            if (thread == 0 || !GetGUIThreadInfo(thread, ref info) || info.Focus == IntPtr.Zero || window != GetForegroundWindow()) return null;
            StringBuilder name = new StringBuilder(128);
            GetClassName(info.Focus, name, name.Capacity);
            string processName;
            using (Process process = Process.GetProcessById(pid)) processName = process.ProcessName;
            return new NativeField(window, info.Focus, name.ToString(), processName);
        }

        internal bool Current()
        {
            if (GetForegroundWindow() != Window) return false;
            int pid;
            uint thread = GetWindowThreadProcessId(Window, out pid);
            ThreadInfo info = new ThreadInfo { Size = Marshal.SizeOf(typeof(ThreadInfo)) };
            return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Focus == Control;
        }

        internal bool Rich { get { return Class.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase); } }
        internal bool NativeEdit { get { return Class.Equals("Edit", StringComparison.OrdinalIgnoreCase) || Rich; } }
        internal bool Browser
        {
            get { return ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                ProcessName.Equals("chromium", StringComparison.OrdinalIgnoreCase); }
        }

        internal int Query(uint message)
        {
            IntPtr result;
            if (Send(Control, message, IntPtr.Zero, IntPtr.Zero, Flags, 500, out result) == IntPtr.Zero) throw new Win32Exception();
            return unchecked((int)result.ToInt64());
        }
        internal bool Editable()
        {
            return NativeEdit && Current() && (GetWindowLong(Control, -16) & (0x20 | 0x800 | 0x08000000)) == 0
                && Query(0x00D2) == 0; // ES_PASSWORD, ES_READONLY, WS_DISABLED, EM_GETPASSWORDCHAR
        }
        internal EditSpan Selection()
        {
            int start, end;
            IntPtr ignored;
            if (ReadSelection(Control, 0x00B0, out start, out end, Flags, 500, out ignored) == IntPtr.Zero) throw new Win32Exception();
            if (start < 0 || end < start) throw new InvalidOperationException();
            return new EditSpan(start, end);
        }
        internal void ReplaceSelection(string value)
        {
            IntPtr ignored;
            if (Replace(Control, 0x00C2, new IntPtr(1), value, Flags, 600, out ignored) == IntPtr.Zero) throw new Win32Exception();
        }
        internal void SwitchLayout(string direction)
        {
            if (Current()) SelectLayout(Control, direction == "EN → RU");
        }

        internal static void SelectLayout(IntPtr window, bool russian)
        {
            int target = russian ? 0x0419 : 0x0409;
            IntPtr[] layouts = new IntPtr[GetKeyboardLayoutList(0, null)];
            int count = GetKeyboardLayoutList(layouts.Length, layouts);
            for (int i = 0; i < count; i++)
                if ((layouts[i].ToInt64() & 0xffff) == target) {
                    PostMessage(window, 0x0050, IntPtr.Zero, layouts[i]); // WM_INPUTLANGCHANGEREQUEST
                    return;
                }
        }
    }
}

