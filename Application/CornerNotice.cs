using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Raskladka
{
    internal sealed class CornerNotice : Window
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI bounds);
        [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
        private readonly TextBlock caption;
        private readonly DispatcherTimer timer;
        internal CornerNotice()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            Focusable = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            caption = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13,
                MaxWidth = 270, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
            caption.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Результат исправления");
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(34,36,41)), CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75,76,80)), BorderThickness = new Thickness(1),
                Padding = new Thickness(14,9,14,9), Child = caption };
            if (SystemParameters.HighContrast)
            {
                ((Border)Content).SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
                caption.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);
            }
            SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80 | 0x20);
            };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += delegate { timer.Stop(); Hide(); };
        }
        internal void Display(string message)
        {
            timer.Stop();
            var area = Forms.Screen.FromHandle(GetForegroundWindow()).WorkingArea;
            caption.Text = message;
            if (!IsVisible) { Opacity = 0; Show(); }
            UpdateLayout();
            var handle = new WindowInteropHelper(this).Handle;
            RectI r;
            GetWindowRect(handle, out r);
            int x = area.Right - (r.Right - r.Left) - 24;
            int y = area.Bottom - (r.Bottom - r.Top) - 24;
            var pointer = Forms.Cursor.Position;
            if (pointer.X >= x - 20 && pointer.Y >= y - 20) y = area.Top + 24;
            SetWindowPos(handle, new IntPtr(-1), x, y, 0, 0, 0x11);
            Opacity = 1;
            timer.Start();
        }
    }
}

