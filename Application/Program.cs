using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Raskladka.Integration;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: AssemblyTitle("Раскладка")]
[assembly: AssemblyDescription("Исправление раскладки выделенного текста")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Raskladka
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int process);

        [STAThread]
        public static void Main(string[] args)
        {
            if (BrowserHost.TryRun(args)) return;
            bool first;
            string sessionName = @"Local\Raskladka.Show." + WindowsIdentity.GetCurrent().User.Value;
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, sessionName, out first))
            {
                if (!first)
                {
                    int process;
                    var panel = FindWindow(null, "Раскладка");
                    if (panel != IntPtr.Zero && GetWindowThreadProcessId(panel, out process) != 0)
                        AllowSetForegroundWindow(process);
                    show.Set();
                    return;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (var desktop = new DesktopController(app))
                {
                    var listener = ThreadPool.RegisterWaitForSingleObject(show, delegate {
                        app.Dispatcher.BeginInvoke(new Action(desktop.ShowPanel));
                    }, null, Timeout.Infinite, false);
                    try { desktop.Start(); app.Run(); }
                    finally { listener.Unregister(null); }
                }
            }
        }
    }

    internal sealed class DesktopController : IDisposable
    {
        private readonly Application app;
        private readonly AppSettings settings;
        private readonly MainWindow panel;
        private readonly EditorService editors;
        private readonly DispatcherTimer positionTimer;
        private HotkeyManager hotkeys;
        private HwndSource source;
        private Forms.NotifyIcon tray;
        private Forms.ContextMenuStrip menu;
        private Drawing.Icon icon;
        private CornerNotice notice;
        private bool exiting;

        internal DesktopController(Application application)
        {
            app = application;
            settings = AppSettings.Load();
            panel = new MainWindow();
            app.MainWindow = panel;
            editors = new EditorService();
            positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            positionTimer.Tick += delegate { positionTimer.Stop(); SavePosition(); };
        }

        internal void Start()
        {
            panel.RegisterBinding = Register;
            panel.PreferencesChanged = PreferencesChanged;
            panel.HideRequested = HidePanel;
            panel.SetPreferences((int)settings.Direction, settings.RecentInput, settings.SwitchLayout,
                settings.AlwaysOnTop, settings.Notify, (int)settings.Motion);
            panel.RestorePosition(settings.Left, settings.Top);
            panel.SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(panel).Handle;
                source = HwndSource.FromHwnd(handle);
                source.AddHook(WindowProc);
                hotkeys = new HotkeyManager(handle);
                if (settings.Hotkey != null && !hotkeys.TrySet(settings.Hotkey))
                    panel.Dispatcher.BeginInvoke(new Action(delegate { panel.ShowError("Это сочетание уже занято"); }));
                var active = hotkeys.Active;
                panel.SetHotkey(active == null ? 0 : (uint)active.Modifiers, active == null ? 0 : (uint)active.VirtualKey);
            };
            panel.LocationChanged += delegate { positionTimer.Stop(); positionTimer.Start(); };
            panel.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!exiting) { e.Cancel = true; HidePanel(); }
            };
            app.SessionEnding += delegate { exiting = true; SavePosition(); };
            icon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Показать окно", null, delegate { ShowPanel(); });
            menu.Items.Add("Настройки", null, delegate { ShowPanel(); panel.ShowSettings(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { Exit(); });
            tray = new Forms.NotifyIcon { Icon = icon, Text = "Раскладка", ContextMenuStrip = menu, Visible = true };
            tray.MouseDoubleClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) ShowPanel(); };
            editors.SetRecentEnabled(settings.RecentInput);
            panel.Show();
        }

        private string Register(uint modifiers, uint key)
        {
            var binding = new HotkeyBinding((int)modifiers, (int)key);
            if (!binding.IsValid) return "Выберите функциональную клавишу или сочетание";
            if (hotkeys == null || !hotkeys.TrySet(binding)) return "Это сочетание уже занято";
            settings.Hotkey = binding;
            if (!settings.Save()) ReportStorageError();
            return null;
        }

        private void PreferencesChanged(int direction, bool recent, bool switchLayout, bool topmost,
            bool notify, int motion)
        {
            if (settings.RecentInput != recent) editors.SetRecentEnabled(recent);
            settings.Direction = (ConversionMode)direction;
            settings.RecentInput = recent;
            settings.SwitchLayout = switchLayout;
            settings.AlwaysOnTop = topmost;
            settings.Notify = notify;
            settings.Motion = (MotionMode)motion;
            panel.Topmost = topmost;
            if (!notify && notice != null) notice.Hide();
            if (!settings.Save()) ReportStorageError();
        }

        private void ReportStorageError()
        {
            panel.Dispatcher.BeginInvoke(new Action(delegate { panel.ShowError("Не удалось сохранить настройки на диске"); }));
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && hotkeys != null && hotkeys.IsCommand(wParam))
            {
                handled = true;
                if (panel.IsRecording && panel.IsActive)
                {
                    var binding = hotkeys.Active;
                    panel.CaptureActiveHotkey((uint)binding.Modifiers, (uint)binding.VirtualKey);
                }
                else Correct();
            }
            return IntPtr.Zero;
        }

        private async void Correct()
        {
            if (exiting || panel.IsActive) return;
            try
            {
                var mode = settings.Direction;
                var response = await editors.CorrectAsync(delegate(string original)
                {
                    var result = LayoutConverter.Convert(original, mode);
                    return new EditConversion(result.Changed ? result.Text : null,
                        result.Direction == ConversionMode.EnToRu ? "EN → RU" : "RU → EN", result.Message);
                }, settings.SwitchLayout);
                if (!exiting) Feedback(response.Success, response.Message, response.Direction);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (!exiting) Feedback(false, "Выделите текст и нажмите сочетание ещё раз", "");
            }
        }

        private void Feedback(bool success, string message, string direction)
        {
            if (success) panel.ShowSuccess(direction == "EN → RU");
            else panel.ShowError(message);
            if (!panel.IsVisible && settings.Notify)
            {
                if (notice == null) notice = new CornerNotice();
                notice.Display(success ? "Исправлено · " + direction : message);
            }
        }

        internal void ShowPanel()
        {
            if (exiting) return;
            if (!panel.IsVisible) panel.Show();
            if (panel.WindowState == WindowState.Minimized) panel.WindowState = WindowState.Normal;
            panel.Activate();
        }

        private void HidePanel()
        {
            SavePosition();
            panel.ShowMini();
            panel.Hide();
        }

        private void SavePosition()
        {
            if (!panel.IsLoaded) return;
            Point p = panel.CompactPosition;
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || (p.X == settings.Left && p.Y == settings.Top)) return;
            settings.Left = p.X;
            settings.Top = p.Y;
            if (!settings.Save()) ReportStorageError();
        }

        private void Exit()
        {
            if (exiting) return;
            exiting = true;
            SavePosition();
            app.Shutdown();
        }

        public void Dispose()
        {
            exiting = true;
            positionTimer.Stop();
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowProc);
            if (hotkeys != null) hotkeys.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (icon != null) icon.Dispose();
            if (menu != null) menu.Dispose();
            editors.Dispose();
        }
    }
}


