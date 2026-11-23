using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml;
using Raskladka;

internal static class CoreTests
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect(string input, ConversionMode mode, string expected, ConversionMode direction)
    {
        var result = LayoutConverter.Convert(input, mode);
        Check(result.Text == expected, "Wrong mapping for " + input);
        Check(result.Direction == direction, "Wrong direction for " + input);
        Check(result.Changed == (input != expected), "Wrong change status for " + input);
    }

    private static void Conversion()
    {
        const ConversionMode auto = ConversionMode.Auto, ru = ConversionMode.EnToRu, en = ConversionMode.RuToEn;
        Expect("Ghbdtn? vbh!", auto, "Привет, мир!", ru);
        Expect("руддщ", auto, "hello", en);
        Expect("lj,hsq ltym", auto, "добрый день", ru);
        Expect("`~", ru, "ёЁ", ru);
        Expect("ёЁ", auto, "`~", en);
        Expect("Qq\tQ\r\n👩‍💻\u0301é", auto, "Йй\tЙ\r\n👩‍💻\u0301é", ru);
        Expect("Ghbdtn мир", auto, "Ghbdtn мир", auto);
        Check(LayoutConverter.Convert("aя", auto).Message == "Выделите фрагмент на одной раскладке", "Mixed hint");
        Expect("Ghbdtn мир", ru, "Привет мир", ru);
        Expect("руддщ world", en, "hello world", en);
        Expect("123 +- 😺?!", auto, "123 +- 😺?!", auto);
        Expect("\r\n🙂", auto, "\r\n🙂", auto);
        Expect(string.Empty, auto, string.Empty, auto);
        Expect(null, auto, null, auto);
        Expect("a1234567890-=", auto, "ф1234567890-=", ru);
        Expect("!@#$%^&*()_+", ru, "!\"№;%:?*()_+", ru);
        Expect("!\"№;%:?*()_+", en, "!@#$%^&*()_+", en);
        Expect("[]{};'\":,.<>`~/?\\|", ru, "хъХЪжэЭЖбюБЮёЁ.,\\/", ru);
        Expect("\\/", en, "\\|", en);
        // Every printable key of US ANSI, including all shifted keys, round-trips.
        for (int i = 32; i <= 126; i++)
        {
            string character = ((char)i).ToString();
            var forward = LayoutConverter.Convert(character, ru);
            var back = LayoutConverter.Convert(forward.Text, en);
            Check(back.Text == character, "Round trip " + character);
        }
    }

    private static void WordAt(string marked, string expected)
    {
        int caret = marked.IndexOf('|');
        string input = marked.Remove(caret, 1);
        int start, end;
        bool found = CaretWord.TryFind(input, caret, out start, out end);
        Check((found ? input.Substring(start, end - start) : null) == expected, "Word at " + marked);
    }

    private static void Words()
    {
        WordAt("Ответ: Ghb|dtn vbh", "Ghbdtn");
        WordAt("Ответ: |Ghbdtn vbh", "Ghbdtn");
        WordAt("Ghbdtn |vbh", "vbh");
        WordAt("Ghbdtn|!", "Ghbdtn");
        WordAt("Ghbdtn!  |", "Ghbdtn");
        WordAt("lj,|hsq ltym", "lj,hsq");
        WordAt("руд|дщ", "руддщ");
        WordAt("руддщ,цщ|кдв", "цщкдв");
        WordAt("Ответ,Gh|bdtn", "Ghbdtn");
        WordAt("до\r\n|Ghbdtn", "Ghbdtn");
        WordAt("Ghbdtn\r\n  |", null);
        WordAt("|", null);
        WordAt(" |  Ghbdtn", null);
        WordAt("Ghbdtn🙂 |", null);
        WordAt("🙂 Ghbdtn\t|", "Ghbdtn");
        WordAt("one..|two", "two");
        WordAt("long-|word!", "long-word");
        WordAt("e\u0301|lan", "e\u0301lan");
        WordAt("𐐀|abc", "𐐀abc");
        int start, end;
        Check(!CaretWord.TryFind("🙂abc", 1, out start, out end), "Split surrogate");
    }

    private static void Settings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RaskladkaTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.xml");
        try
        {
            var settings = AppSettings.Load(path);
            Check(settings.Hotkey == null && !settings.SwitchLayout && !settings.Notify && !settings.AlwaysOnTop, "Defaults");
            Check(settings.Motion == MotionMode.System && settings.Direction == ConversionMode.Auto && double.IsNaN(settings.Left), "Default modes");
            settings.Hotkey = new HotkeyBinding(2, 0x4A);
            settings.Notify = settings.AlwaysOnTop = settings.SwitchLayout = true;
            settings.Direction = ConversionMode.EnToRu;
            settings.Motion = MotionMode.Minimal;
            settings.Left = -1012.25;
            settings.Top = 72.5;
            Check(settings.Save(path), "First save");
            var legacyFile = new XmlDocument();
            legacyFile.Load(path);
            legacyFile.DocumentElement.SetAttribute("lastTab", "2");
            legacyFile.DocumentElement.SetAttribute("recentInput", "true");
            legacyFile.Save(path);
            var loaded = AppSettings.Load(path);
            Check(settings.Hotkey.Equals(loaded.Hotkey) && loaded.SwitchLayout && loaded.Notify && loaded.AlwaysOnTop, "Restored switches");
            Check(loaded.Direction == settings.Direction && loaded.Motion == settings.Motion && loaded.Left == -1012.25 && loaded.Top == 72.5, "Restored modes and coordinates from tabbed settings");
            loaded.Hotkey = null;
            Check(loaded.Save(path) && AppSettings.Load(path).Hotkey == null, "Replace");
            legacyFile.Load(path);
            Check(!legacyFile.DocumentElement.HasAttribute("recentInput"), "Obsolete input tracking setting removed on save");
            Check(!legacyFile.DocumentElement.HasAttribute("lastTab"), "Obsolete tab omitted on save");
            string committed = File.ReadAllText(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            Check(!settings.Save(path) && File.ReadAllText(path) == committed, "Failure must retain previous settings");
            File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, "<settings");
            Check(AppSettings.Load(path).Hotkey == null, "Broken file fallback");
            File.WriteAllText(path, "<settings modifiers='0' virtualKey='65' direction='999' motion='50' lastTab='-1' notify='true' left='Infinity' />");
            loaded = AppSettings.Load(path);
            Check(loaded.Hotkey == null && loaded.Direction == ConversionMode.Auto && loaded.Motion == MotionMode.System && loaded.Notify && double.IsNaN(loaded.Left), "Invalid values fallback");
            Check(Directory.GetFiles(directory).Length == 1, "Temporary files cleaned up");
        }
        finally
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static void Bindings()
    {
        Check(!new HotkeyBinding(0, 0x41).IsValid && !new HotkeyBinding(0, 0x20).IsValid, "Plain typing");
        Check(!new HotkeyBinding(0, 0x7B).IsValid && !new HotkeyBinding(1, 0x10).IsValid, "Reserved keys");
        Check(new HotkeyBinding(0, 0x77).IsValid && new HotkeyBinding(0, 0x77).ToString() == "F8", "Function key");
        Check(new HotkeyBinding(6, 0x4C).IsValid && new HotkeyBinding(6, 0x4C).ToString() == "Ctrl + Shift + L", "Chord");

        // Message-only windows exercise the real RegisterHotKey service without focusing
        // another window, synthesizing input or interfering with the clipboard.
        IntPtr window1 = MessageWindow(), window2 = MessageWindow();
        try
        {
            using (var first = new HotkeyManager(window1))
            using (var second = new HotkeyManager(window2))
            {
                var a = new HotkeyBinding(7, 0x83); // Ctrl+Shift+Alt+F20
                var b = new HotkeyBinding(7, 0x84);
                Check(first.TrySet(a) && second.TrySet(b), "Claim test keys");
                Check(!first.TrySet(b) && first.Active.Equals(a), "Keep original on conflict");
                Check(!second.TrySet(a), "Old binding still registered with Windows");
                Check(first.TrySet(a), "Reassign same binding");
                second.Dispose();
                Check(first.TrySet(b) && first.Active.Equals(b), "Freed key reusable");
                using (var third = new HotkeyManager(window2))
                    Check(third.TrySet(a), "Changing binding releases previous key");
                Check(first.TrySet(null) && first.Active == null, "Clear");
            }
        }
        finally
        {
            DestroyWindow(window1);
            DestroyWindow(window2);
        }
    }

    private static IntPtr MessageWindow()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Cannot create message window");
        return hwnd;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    public static int Main()
    {
        try
        {
            Conversion();
            Words();
            Settings();
            Bindings();
            Console.WriteLine("Passed {0} checks (conversion, caret words, settings, Win32 hotkeys).", assertions);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
