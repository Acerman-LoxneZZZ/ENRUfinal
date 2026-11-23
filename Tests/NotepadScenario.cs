using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
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
        bool active = GetForegroundWindow() == window;
        var field = (NativeField)Activator.CreateInstance(typeof(NativeField), BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { window, edit, "Edit", "notepad" }, null);
        using (var service = new EditorService())
        {
            Func<Func<string, EditConversion>, EditorResult> correct;
            if (active) correct = convert => Correct(service, convert);
            else
            {
                log.WriteLine("Using the production selection transaction with a fixture focus check on the private desktop.");
                var element = AutomationElement.FromHandle(edit);
                correct = convert => EditorService.CorrectSelection(field, element, field.Selection(), convert,
                    delegate { int pid; return GetWindowThreadProcessId(edit, out pid) != 0 && pid == notepad.Id; });
                bool invoked = false;
                var refused = Correct(service, delegate(string text) { invoked = true; return Convert(text); });
                if (refused.Success || invoked) throw new InvalidOperationException("Public command must reject an inactive field");
                log.WriteLine("PASS Public command rejects inactive field");
            }
            return Scenarios(edit, field, correct, log);
        }
    }

    private static int Scenarios(IntPtr edit, NativeField field, Func<Func<string, EditConversion>, EditorResult> correct, TextWriter log)
    {
        int failed = 0;
        Action<bool, string> check = delegate(bool value, string title) {
            log.WriteLine((value ? "PASS " : "FAIL ") + title);
            if (!value) failed++;
        };
        uint clipboard = GetClipboardSequenceNumber();
        Action<string, string, int, string> checkWord = delegate(string original, string expected, int at, string label) {
            Seed(edit, original, at, at);
            var result = correct(Convert);
            check(result.Success && ReadFixture(edit) == expected, label + " (" + result.Message + ")");
            check(field.Selection().Same(new EditSpan(at, at)), label + " retains original caret");
            Send(edit, 0x0102, new IntPtr(0x1A), IntPtr.Zero); // WM_CHAR, Ctrl+Z
            check(ReadFixture(edit) == original, label + " undone in one edit");
        };

        const string input = "Ответ: Ghbdtn!", output = "Ответ: Привет!";
        int start = input.IndexOf('G'), end = input.IndexOf('!');
        Seed(edit, input, start, end);
        var response = correct(Convert);
        check(response.Success && response.Direction == "EN → RU" && ReadFixture(edit) == output, "Convert exactly selected text");
        check(field.Selection().Same(new EditSpan(end, end)), "Caret after replaced selection");
        Send(edit, 0x0102, new IntPtr(0x1A), IntPtr.Zero);
        check(ReadFixture(edit) == input, "One undo restores the selected text");
        Select(edit, start, end);
        correct(Convert);
        Send(edit, 0x0102, new IntPtr('★'), IntPtr.Zero);
        check(ReadFixture(edit) == "Ответ: Привет★!", "Typing continues at the corrected caret");

        Seed(edit, "Ответ: Ghbdtn? vbh!", 7, 19);
        response = correct(Convert);
        check(response.Success && ReadFixture(edit) == "Ответ: Привет, мир!", "A multiword selection takes priority over single word");
        checkWord(input, output, end, "End of word");
        checkWord(input, output, start + 3, "Middle of word");
        checkWord(input, output, start, "Start of word");
        checkWord("Уже Ghbdtn!  ", "Уже Привет!  ", 13, "Word before punctuation and spaces");
        checkWord("Префикс🙂\r\nGhbdtn vbh", "Префикс🙂\r\nПривет vbh", 14, "Second line following CRLF and emoji");
        checkWord("abc руддщ xyz", "abc hello xyz", 7, "Reverse layout inside word");
        checkWord("До lj,hsq ltym", "До добрый ltym", 9, "Internal keyboard punctuation");

        Seed(edit, "aЯ", 0, 2);
        response = correct(Convert);
        check(!response.Success && ReadFixture(edit) == "aЯ", "Mixed selection retained");
        foreach (string original in new[] { "", "Ghbdtn\r\n", "Ghbdtn\r\n   " }) {
            Seed(edit, original, original.Length, original.Length);
            bool invoked = false;
            response = correct(delegate(string text) { invoked = true; return Convert(text); });
            check(!response.Success && !invoked && ReadFixture(edit) == original, "No word on the current line");
        }
        Seed(edit, input, end, end);
        Send(edit, 0x00CF, new IntPtr(1), IntPtr.Zero); // EM_SETREADONLY
        bool converted = false;
        response = correct(delegate(string text) { converted = true; return Convert(text); });
        check(!response.Success && !converted && ReadFixture(edit) == input, "Read-only field rejected before conversion");
        Send(edit, 0x00CF, IntPtr.Zero, IntPtr.Zero);

        Seed(edit, input, end, end);
        response = correct(delegate(string text) { Select(edit, 0, 0); return Convert(text); });
        check(!response.Success && field.Selection().Same(new EditSpan(0, 0)) && ReadFixture(edit) == input, "Moved caret cancels prepared word");
        Seed(edit, input, start, end);
        response = correct(delegate(string text) { Select(edit, 1, 3); return Convert(text); });
        check(!response.Success && field.Selection().Same(new EditSpan(1, 3)) && ReadFixture(edit) == input, "Changed selection cancels prepared replacement");
        Seed(edit, input, start + 1, start + 1);
        response = correct(delegate(string text) { Seed(edit, "Ответ: Ghbdtns!", start + 1, start + 1); return Convert(text); });
        check(!response.Success && ReadFixture(edit) == "Ответ: Ghbdtns!", "Word boundary changed during conversion");
        check(clipboard == GetClipboardSequenceNumber(), "Clipboard sequence number remained unchanged");
        return failed == 0 ? 0 : 1;
    }
}
