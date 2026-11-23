using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Raskladka.Integration
{
    // A bound and two counters only. The hook is out-of-context and listens to control
    // notifications. Rich Edit exposes the provenance of the last edit via its undo stack.
    internal sealed class RecentNative : IDisposable
    {
        private delegate void EventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, EventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        private readonly EventProc callback;
        private readonly object gate = new object();
        private readonly IntPtr[] hooks = new IntPtr[4];
        private NativeField field;
        private int length, caret, start = -1, priorRedo;
        private long lastEdit;
        private bool enabled;
        internal RecentNative() { callback = OnEvent; }
        internal void Enable(bool value)
        {
            lock (gate) {
                if (enabled == value) return;
                enabled = value;
                if (value) {
                    uint[] events = { 3, 0x8005, 0x800E, 0x8014 }; // foreground, focus, value, text selection
                    for (int i = 0; i < hooks.Length; i++) hooks[i] = SetWinEventHook(events[i], events[i], IntPtr.Zero, callback, 0, 0, 2);
                    if (Array.IndexOf(hooks, IntPtr.Zero) < 0) Bind();
                    else Enable(false);
                } else {
                    for (int i = 0; i < hooks.Length; i++) { if (hooks[i] != IntPtr.Zero) UnhookWinEvent(hooks[i]); hooks[i] = IntPtr.Zero; }
                    field = null; start = -1;
                }
            }
        }
        private void Bind()
        {
            field = null; start = -1;
            try {
                NativeField next = NativeField.Capture();
                if (next == null || !next.Rich || !next.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase) || !next.Editable()) return;
                EditSpan sel = next.Selection();
                length = next.Query(0x000E); // WM_GETTEXTLENGTH (no text is requested)
                priorRedo = next.Query(0x0457); // EM_GETREDONAME
                caret = sel.Empty ? sel.End : -1; field = next;
            } catch (Exception) { field = null; }
        }
        private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            lock (gate) {
                if (!enabled) return;
                if (evt == 3 || evt == 0x8005) { Bind(); return; }
                if (field == null || field.Control != hwnd) return;
                try {
                    if (!field.Editable()) { field = null; start = -1; return; }
                    EditSpan sel = field.Selection();
                    int nextLength = field.Query(0x000E);
                    int delta = nextLength - length;
                    int undo = field.Query(0x0456), redo = field.Query(0x0457);
                    long now = Stopwatch.GetTimestamp();
                    if (delta != 0) {
                        // A multi-character edit, paste, undo/redo, selected overwrite or an
                        // uncorrelated caret movement ends the series. Never infer its text.
                        bool typed = sel.Empty && caret >= 0 && sel.End == caret + delta &&
                            priorRedo == 0 && redo == 0 && (undo == 1 || (undo == 2 && delta == -1));
                        if (!typed || Math.Abs((long)delta) != 1) start = -1;
                        else if (delta > 0) {
                            if (start < 0 || now - lastEdit > Stopwatch.Frequency * 8) start = caret;
                            lastEdit = now;
                        } else if (start >= 0 && sel.End > start && now - lastEdit <= Stopwatch.Frequency * 8) lastEdit = now;
                        else start = -1;
                    } else if (!sel.Empty || caret != sel.End || redo != priorRedo) start = -1;
                    caret = sel.Empty ? sel.End : -1; length = nextLength; priorRedo = redo;
                } catch (Exception) { field = null; start = -1; }
            }
        }
        internal EditSpan? Take(NativeField target, int position)
        {
            lock (gate) {
                if (!enabled || field == null || target.Control != field.Control || start < 0 ||
                    position != caret || Stopwatch.GetTimestamp() - lastEdit > Stopwatch.Frequency * 8) return null;
                return new EditSpan(start, caret);
            }
        }
        internal void Reset() { lock (gate) start = -1; }
        public void Dispose() { Enable(false); }
    }
}
