using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Raskladka;

// Observe screen coordinates even between a native move and the next WPF frame.
// Rendering a cropped image of the panel alone cannot catch that displacement.
internal static class WindowMotionTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--worker")
        {
            PrivateDesktop.RequireTestDesktop();
            using (var log = File.CreateText(args[1]))
                try { Verify(log); return 0; }
                catch (Exception ex) { log.WriteLine(ex); return 1; }
        }
        string logFile = Path.GetTempFileName();
        try
        {
            using (var desktop = new PrivateDesktop())
            using (var self = Process.GetCurrentProcess())
            using (var child = desktop.Start(self.MainModule.FileName, "--worker \"" + logFile + "\""))
            {
                if (!child.WaitForExit(15000)) { child.Kill(); throw new TimeoutException("Motion test timed out."); }
                Console.Write(File.ReadAllText(logFile));
                return child.ExitCode;
            }
        }
        finally { File.Delete(logFile); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        stop.Tick += delegate { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Verify(TextWriter log)
    {
        new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            var area = SystemParameters.WorkArea;
            window.RestorePosition(area.Left + 20, area.Top + 20);
            window.SetPreferences(0, false, false, false, false, 1);
            window.SetHotkey(0, 0x77);
            window.Show();
            Pump(350);
            var logo = (FrameworkElement)window.FindName("Signature");
            var surface = (FrameworkElement)window.FindName("Surface");
            var source = (HwndSource)PresentationSource.FromVisual(window);
            var start = logo.PointToScreen(new Point());
            double largestDrift = 0;
            bool intermediate = false;
            Action sample = delegate
            {
                var delta = logo.PointToScreen(new Point()) - start;
                largestDrift = Math.Max(largestDrift, Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y)));
                intermediate |= surface.ActualWidth > 324 && surface.ActualWidth < 576;
            };
            EventHandler onFrame = delegate { sample(); };
            HwndSourceHook onMessage = delegate(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
            {
                if (msg == 0x0047) sample(); // WM_WINDOWPOSCHANGED
                return IntPtr.Zero;
            };
            source.AddHook(onMessage);
            CompositionTarget.Rendering += onFrame;
            try
            {
                window.ShowSettings();
                Pump(900);
                if (Math.Abs(surface.ActualWidth - 580) > 1)
                    throw new InvalidOperationException("Settings did not reach their expanded width.");
                window.ShowMini(); Pump(90);
                window.ShowSettings(); Pump(130);
                window.ShowMini(); Pump(900);
                sample();
            }
            finally { CompositionTarget.Rendering -= onFrame; source.RemoveHook(onMessage); }
            log.WriteLine("Largest screen-space logo displacement: {0:F2} px", largestDrift);
            if (largestDrift > 1 || !intermediate || Math.Abs(surface.ActualWidth - 320) > 1)
                throw new InvalidOperationException("The panel jumped, skipped its transition, or failed to return to mini.");
            VerifySettingsList(window);
        }
        finally { window.Close(); Application.Current.Shutdown(); }
    }

    private static void VerifySettingsList(MainWindow window)
    {
        window.ShowSettings();
        Pump(900);
        var scroll = (ScrollViewer)window.FindName("SettingsScroll");
        var record = (Button)window.FindName("SettingsRecord");
        var last = (RadioButton)window.FindName("MinimalMotion");
        if (!record.IsVisible || !last.IsVisible || scroll.ScrollableHeight <= 0)
            throw new InvalidOperationException("The first and last settings must share a scrollable list.");
        record.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent });
        Pump(100);
        if (scroll.VerticalOffset <= 0)
            throw new InvalidOperationException("The wheel did not scroll over the shortcut control.");

        window.ShowError("Выделите текст и нажмите сочетание ещё раз");
        scroll.ScrollToBottom();
        Pump(120);
        double top = last.TranslatePoint(new Point(), scroll).Y;
        double bottom = top + last.ActualHeight;
        var surface = (FrameworkElement)window.FindName("Surface");
        if (top < -1 || bottom > scroll.ViewportHeight + 1 ||
            last.TranslatePoint(new Point(0, last.ActualHeight), surface).Y > surface.ActualHeight)
            throw new InvalidOperationException("A status hint clipped the last setting at the end of the list.");
    }
}
