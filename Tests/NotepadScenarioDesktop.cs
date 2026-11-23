using System;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class NotepadScenarioDesktop
{
    [MTAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker")
        {
            using (var log = new StreamWriter(args[2], false, new UTF8Encoding(false)) { AutoFlush = true })
                try {
                    PrivateDesktop.RequireTestDesktop();
                    using (var notepad = Process.GetProcessById(int.Parse(args[1]))) return NotepadScenario.Run(notepad, log);
                }
                catch (Exception ex) { log.WriteLine(ex); return 1; }
        }
        string logPath = Path.GetTempFileName();
        try
        {
            using (var desktop = new PrivateDesktop())
            using (var self = Process.GetCurrentProcess())
            using (var notepad = desktop.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), ""))
            {
                try {
                    using (var child = desktop.Start(self.MainModule.FileName, "--worker " + notepad.Id + " \"" + logPath + "\""))
                    {
                        if (!child.WaitForExit(45000)) { child.Kill(); throw new TimeoutException("Isolated Notepad worker timed out."); }
                        Console.Write(File.ReadAllText(logPath, Encoding.UTF8));
                        return child.ExitCode;
                    }
                } finally {
                    // Always clean up the exact scratch process, including on worker timeout.
                    if (!notepad.HasExited) { notepad.Kill(); notepad.WaitForExit(2000); }
                }
            }
        }
        finally { File.Delete(logPath); }
    }
}
