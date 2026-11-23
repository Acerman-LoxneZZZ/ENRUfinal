using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Raskladka;
using Raskladka.Integration;

internal static class NotepadScenario
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr Send(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetString(IntPtr hwnd, int message, IntPtr w, string value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ReadString(IntPtr hwnd, int message, IntPtr w, StringBuilder text);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr GetSelection(IntPtr hwnd, int message, out int start, out int end);

    private static IntPtr Child(Process process, string parentClass, string childClass)
    {
        var timer = Stopwatch.StartNew();
        do {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, parentClass, null)) != IntPtr.Zero)
            {
                int pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid != process.Id) continue;
                var child = FindWindowEx(window, IntPtr.Zero, childClass, null);
                if (child != IntPtr.Zero) return child;
            }
            Thread.Sleep(40);
        } while (timer.ElapsedMilliseconds < 4500 && !process.HasExited);
        throw new InvalidOperationException("Classic Notepad EDIT control not found on the test desktop");
    }

    private static string ReadFixture(IntPtr edit)
    {
        var result = new StringBuilder(256);
        ReadString(edit, 0x000D, new IntPtr(result.Capacity), result);
        return result.ToString();
    }

    private static void Seed(IntPtr edit, string text, int start, int end)
    {
        SetString(edit, 0x000C, IntPtr.Zero, text);
        Send(edit, 0x00C5, IntPtr.Zero, IntPtr.Zero); // EM_EMPTYUNDOBUFFER
        Select(edit, start, end);
    }

    private static void Select(IntPtr edit, int start, int end)
    {
        Send(edit, 0x00B1, new IntPtr(start), new IntPtr(end));
    }

    private static EditConversion Convert(string text)
    {
        var change = LayoutConverter.Convert(text, ConversionMode.Auto);
        string direction = change.Direction == ConversionMode.EnToRu ? "EN → RU" : "RU → EN";
        return new EditConversion(change.Changed ? change.Text : null, direction, change.Message);
    }

    private static EditorResult Correct(EditorService service, Func<string, EditConversion> convert)
    {
        var task = service.CorrectAsync(convert, false);
        if (!task.Wait(4000)) throw new TimeoutException("EditorService.CorrectAsync did not return");
        return task.Result;
    }

    internal static int Run(Process notepad, TextWriter log)
    {
        IntPtr edit = Child(notepad, "Notepad", "Edit");
        int ownedPid;
        GetWindowThreadProcessId(edit, out ownedPid);
        if (ownedPid != notepad.Id) throw new InvalidOperationException("Unexpected test window owner");
        IntPtr window = GetParent(edit);
        SetForegroundWindow(window);
        Thread.Sleep(120);
        log.WriteLine("Classic Notepad " + FileVersionInfo.GetVersionInfo(notepad.MainModule.FileName).FileVersion + " on private desktop");
        if (GetForegroundWindow() != window) return IsolatedControls(edit, window, log);
        return Scenarios(edit, window, log);
    }

    // Windows may deny foreground activation on an inactive desktop. In that case
    // exercise the production Win32 adapter directly and label the scope explicitly.
    private static int IsolatedControls(IntPtr edit, IntPtr window, TextWriter log)
    {
        log.WriteLine("No foreground on private desktop. Testing the real EDIT control, UIA range, LayoutConverter and NativeField operations directly.");
        var field = (NativeField)Activator.CreateInstance(typeof(NativeField), BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { window, edit, "Edit", "notepad" }, null);
        uint initialClipboard = GetClipboardSequenceNumber();
        int failures = 0;
        Action<bool, string> check = delegate(bool value, string title) {
            log.WriteLine((value ? "PASS " : "FAIL ") + title);
            if (!value) failures++;
        };
        const string source = "Ответ: Ghbdtn!";
        int start = source.IndexOf('G'), end = source.IndexOf('!');
        Seed(edit, source, start, end);
        var element = AutomationElement.FromHandle(edit);
        var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
        var range = pattern.GetSelection()[0];
        check(range.GetText(-1) == "Ghbdtn", "UIA reads only selected word");
        check(field.Selection().Same(new EditSpan(start, end)), "Native selection matches range");
        var converted = Convert(range.GetText(-1));
        field.ReplaceSelection(converted.Replacement);
        check(ReadFixture(edit) == "Ответ: Привет!", "Production EM_REPLACESEL changes only target");
        check(field.Selection().Same(new EditSpan(end, end)), "Collapsed caret before !");
        var after = pattern.GetSelection()[0].Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.Start);
        check(after.GetText(-1) == converted.Replacement, "UIA postcondition with previous start pointer");
        Send(edit, 0x0102, new IntPtr(0x1A), IntPtr.Zero);
        check(ReadFixture(edit) == source, "One Ctrl+Z character undoes the change");
        Select(edit, start, end);
        field.ReplaceSelection(converted.Replacement);
        Send(edit, 0x0102, new IntPtr('★'), IntPtr.Zero);
        check(ReadFixture(edit) == "Ответ: Привет★!", "Continue typing at corrected caret");
        Seed(edit, "[руддщ]", 1, 6);
        field.ReplaceSelection(Convert(pattern.GetSelection()[0].GetText(-1)).Replacement);
        check(ReadFixture(edit) == "[hello]", "Reverse conversion inside brackets");
        Seed(edit, source, start, end);
        bool invoked = false;
        using (var service = new EditorService())
        {
            var result = Correct(service, delegate(string s) { invoked = true; return Convert(s); });
            check(!result.Success && !invoked && ReadFixture(edit) == source, "Public command skips inactive field");
        }
        check(GetClipboardSequenceNumber() == initialClipboard, "Clipboard sequence number remained unchanged");
        log.WriteLine("Private desktop did not exercise a physical hotkey, active-field UX, or recent typing.");
        return failures == 0 ? 0 : 1;
    }

    private static int Scenarios(IntPtr edit, IntPtr window, TextWriter log)
    {
        int failed = 0;
        Action<bool, string> check = delegate(bool value, string title) {
            log.WriteLine((value ? "PASS " : "FAIL ") + title);
            if (!value) failed++;
        };
        uint clipboard = GetClipboardSequenceNumber();
        using (var service = new EditorService())
        {
            const string input = "Ответ: Ghbdtn!", output = "Ответ: Привет!";
            int start = input.IndexOf('G'), end = input.IndexOf('!');
            Seed(edit, input, start, end);
            var result = Correct(service, Convert);
            check(result.Success && result.Direction == "EN → RU", "Confirmed result EN → RU (" + result.Message + ")");
            check(ReadFixture(edit) == output, "Replace exactly the selected word");
            int caret, tail;
            GetSelection(edit, 0x00B0, out caret, out tail);
            check(caret == end && tail == end && GetForegroundWindow() == window, "Caret before ! and original editor active");
            Send(edit, 0x0102, new IntPtr(0x1A), IntPtr.Zero); // WM_CHAR, Ctrl+Z
            check(ReadFixture(edit) == input, "One Ctrl+Z character restores the original word");
            Select(edit, start, end);
            Correct(service, Convert);
            Send(edit, 0x0102, new IntPtr('★'), IntPtr.Zero);
            check(ReadFixture(edit) == "Ответ: Привет★!", "Typing continues at the corrected caret");

            Seed(edit, "[руддщ]", 1, 6);
            result = Correct(service, Convert);
            check(result.Success && result.Direction == "RU → EN" && ReadFixture(edit) == "[hello]", "RU → EN");
            Seed(edit, "Ghbdtn? vbh!", 0, 12);
            result = Correct(service, Convert);
            check(result.Success && ReadFixture(edit) == "Привет, мир!", "Shift punctuation");
            Seed(edit, "aЯ", 0, 2);
            result = Correct(service, Convert);
            check(!result.Success && ReadFixture(edit) == "aЯ" && result.Message == "Выделите фрагмент на одной раскладке", "Mixed selection");

            Seed(edit, input, end, end);
            bool read = false;
            Func<string, EditConversion> detectRead = delegate(string s) { read = true; return Convert(s); };
            result = Correct(service, detectRead);
            check(!result.Success && !read && ReadFixture(edit) == input, "Empty selection with recent input disabled");
            Seed(edit, "", 0, 0);
            result = Correct(service, detectRead);
            check(!result.Success && !read && ReadFixture(edit) == "", "Empty document");

            Seed(edit, input, start, end);
            Send(edit, 0x00CF, new IntPtr(1), IntPtr.Zero); // EM_SETREADONLY
            result = Correct(service, detectRead);
            check(!result.Success && !read && ReadFixture(edit) == input, "Read-only selection rejected before conversion");
            Send(edit, 0x00CF, IntPtr.Zero, IntPtr.Zero);

            Seed(edit, input, start, end);
            bool changedDuringRead = false;
            result = Correct(service, delegate(string s) { changedDuringRead = true; Select(edit, 0, 0); return Convert(s); });
            check(changedDuringRead && !result.Success && ReadFixture(edit) == input, "Selection changed between read and write");
        }
        check(clipboard == GetClipboardSequenceNumber(), "Clipboard sequence number remained unchanged");
        return failed == 0 ? 0 : 1;
    }
}
