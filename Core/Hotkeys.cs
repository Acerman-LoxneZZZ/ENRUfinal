using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Raskladka
{
    public sealed class HotkeyBinding : IEquatable<HotkeyBinding>
    {
        public int Modifiers { get; private set; }
        public int VirtualKey { get; private set; }

        public HotkeyBinding(int modifiers, int virtualKey)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
        }

        public bool IsValid
        {
            get
            {
                if ((Modifiers & ~15) != 0 || VirtualKey == 0x7B) return false; // F12 belongs to the debugger.
                if (VirtualKey >= 0x70 && VirtualKey <= 0x87) return true; // F1–F24.
                if (Modifiers == 0) return false;
                return VirtualKey == 0x08 || VirtualKey == 0x09 || VirtualKey == 0x0D || VirtualKey == 0x1B ||
                       (VirtualKey >= 0x20 && VirtualKey <= 0x28) || VirtualKey == 0x2C ||
                       VirtualKey == 0x2D || VirtualKey == 0x2E || (VirtualKey >= 0x30 && VirtualKey <= 0x39) ||
                       (VirtualKey >= 0x41 && VirtualKey <= 0x5A) || (VirtualKey >= 0x60 && VirtualKey <= 0x6F) ||
                       (VirtualKey >= 0xBA && VirtualKey <= 0xC0) || (VirtualKey >= 0xDB && VirtualKey <= 0xDF) || VirtualKey == 0xE2;
            }
        }

        public string[] GetKeycaps()
        {
            var caps = new List<string>(5);
            if ((Modifiers & 2) != 0) caps.Add("Ctrl");
            if ((Modifiers & 1) != 0) caps.Add("Alt");
            if ((Modifiers & 4) != 0) caps.Add("Shift");
            if ((Modifiers & 8) != 0) caps.Add("Win");
            if (VirtualKey != 0) caps.Add(KeyName(VirtualKey));
            return caps.ToArray();
        }

        private static string KeyName(int key)
        {
            if ((key >= 0x41 && key <= 0x5A) || (key >= 0x30 && key <= 0x39)) return ((char)key).ToString();
            if (key >= 0x70 && key <= 0x87) return "F" + (key - 0x6F);
            if (key >= 0x60 && key <= 0x69) return "Num " + (key - 0x60);
            switch (key)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x20: return "Пробел";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PrtSc";
                case 0x2D: return "Ins";
                case 0x2E: return "Del";
            }
            var name = new StringBuilder(64);
            uint scanCode = MapVirtualKey((uint)key, 0);
            if (scanCode != 0 && GetKeyNameText((int)(scanCode << 16), name, name.Capacity) > 0)
                return name.ToString();
            return "VK " + key.ToString("X2");
        }

        public override string ToString() { return string.Join(" + ", GetKeycaps()); }
        public bool Equals(HotkeyBinding other)
        {
            return other != null && Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
        }
        public override bool Equals(object obj) { return Equals(obj as HotkeyBinding); }
        public override int GetHashCode() { return (Modifiers * 397) ^ VirtualKey; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameText(int keyData, StringBuilder name, int length);
    }

    // Construct, register and dispose on the thread that owns the target window.
    public sealed class HotkeyManager : IDisposable
    {
        public const int WM_HOTKEY = 0x0312;
        private const uint NoRepeat = 0x4000;
        private readonly IntPtr window;
        private int activeId;
        private int nextId = 0x3100;
        private bool disposed;

        public HotkeyBinding Active { get; private set; }

        public HotkeyManager(IntPtr hwnd) { window = hwnd; }

        public bool TrySet(HotkeyBinding binding)
        {
            if (disposed) return false;
            if (Equals(binding, Active)) return true;
            if (binding == null)
            {
                if (!UnregisterHotKey(window, activeId)) return false;
                Active = null;
                return true;
            }
            if (!binding.IsValid) return false;
            int candidateId = nextId++;
            if (nextId > 0xBFFF) nextId = 0x3100;
            if (candidateId == activeId) candidateId = nextId++;
            if (!RegisterHotKey(window, candidateId, (uint)binding.Modifiers | NoRepeat, (uint)binding.VirtualKey)) return false;

            if (Active != null && !UnregisterHotKey(window, activeId))
            {
                UnregisterHotKey(window, candidateId);
                return false;
            }
            Active = binding;
            activeId = candidateId;
            return true;
        }

        public bool IsCommand(IntPtr wParam)
        {
            return Active != null && wParam == new IntPtr(activeId);
        }

        public void Dispose()
        {
            if (Active != null) UnregisterHotKey(window, activeId);
            Active = null;
            disposed = true;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
