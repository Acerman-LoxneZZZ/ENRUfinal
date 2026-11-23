using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Raskladka
{
    public partial class MainWindow
    {
        private const double MiniWidth = 320, MiniHeight = 100, SettingsWidth = 580, SettingsHeight = 460;
        private readonly PanelSpring panelWidth = new PanelSpring(380);
        private readonly PanelSpring panelHeight = new PanelSpring(300);
        private bool panelAnimating;
        private long lastFrame;
        private TimeSpan lastRenderTime;
        private double compactRevealHeight, expandedRevealHeight;
        private double compactWidth, expandedWidth;
        private double horizontalPivot, verticalPivot;
        private double textScale = 1;
        private Vector compactOffset;
        private Point? requestedPosition;
        private Dictionary<string, Brush> standardPalette;

        public Point CompactPosition
        {
            get { return requestedPosition ?? (new Point(Left, Top) + compactOffset); }
        }

        public void RestorePosition(double x, double y)
        {
            if (!IsCoordinate(x) || !IsCoordinate(y)) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            requestedPosition = new Point(x, y);
            if (IsLoaded) SetGeometry(false);
            else { Left = x; Top = y; }
        }

        private static bool IsCoordinate(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) < 1000000;
        }

        private void DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            var node = e.OriginalSource as DependencyObject;
            while (node != null && node != sender)
            {
                if (node is ButtonBase) return;
                var visual = node as Visual;
                node = visual == null ? LogicalTreeHelper.GetParent(node) : VisualTreeHelper.GetParent(visual);
            }
            // Only the two labelled drag areas call this method. The transparent
            // space around the compact surface belongs to the application below.
            try { DragMove(); } catch (InvalidOperationException) { return; }
            SetGeometry(false);
        }

        private void ChangeExpansion(bool open)
        {
            expanded = open;
            SettingsGlyph.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            BackGlyph.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            string label = open ? "Вернуться в мини-окно" : "Открыть настройки";
            SettingsButton.ToolTip = label;
            AutomationProperties.SetName(SettingsButton, label);
            MiniRecord.Visibility = Visibility.Visible;
            MiniRecord.IsHitTestVisible = !open;
            MiniRecord.Focusable = !open;
            if (!open && Details.IsKeyboardFocusWithin) SettingsButton.Focus();
            if (open) Details.Visibility = Visibility.Visible;
            Details.IsHitTestVisible = open;
            Details.Focusable = false;
            Details.IsEnabled = open;
            SetGeometry(IsLoaded && IsVisible && Animate);
        }

        private double CompactHeight()
        {
            double available = Math.Max(1, compactWidth - Inner.Margin.Left - Inner.Margin.Right);
            MiniRecord.Measure(new Size(available, double.PositiveInfinity));
            double statusWidth = Math.Max(1, available - Signature.Width - MiniRecord.DesiredSize.Width);
            Status.Measure(new Size(statusWidth, double.PositiveInfinity));
            Summary.Height = Math.Max(Summary.MinHeight, Math.Max(Status.DesiredSize.Height, MiniRecord.DesiredSize.Height));
            TitleBar.Measure(new Size(available, double.PositiveInfinity));
            double extra = 0;
            if (Hint.Visibility == Visibility.Visible)
            {
                HintFrame.Measure(new Size(available, double.PositiveInfinity));
                extra = HintFrame.DesiredSize.Height;
            }
            double height = Inner.Margin.Top + Inner.Margin.Bottom + TitleBar.DesiredSize.Height
                + Summary.Margin.Top + Summary.Height + Summary.Margin.Bottom + extra;
            return Math.Max(MiniHeight, Math.Ceiling(height));
        }

        private void SizeToSummary()
        {
            if (IsLoaded && !expanded) SetGeometry(IsVisible && Animate);
        }

        private void RefreshPanelMeasure()
        {
            if (!IsLoaded || compactWidth <= 0) return;
            double before = Summary.Height;
            CompactHeight();
            if (Math.Abs(before - Summary.Height) > 0.01)
                SetGeometry(IsVisible && Animate);
        }

        private void SetGeometry(bool animate)
        {
            Point origin = CompactPosition;
            if (!IsCoordinate(origin.X) || !IsCoordinate(origin.Y))
            {
                Rect primary = SystemParameters.WorkArea;
                origin = new Point(primary.Left + (primary.Width - MiniWidth * textScale) / 2,
                    primary.Top + (primary.Height - MiniHeight * textScale) / 2);
            }
            Rect area = WorkArea(origin);
            compactWidth = Math.Min(MiniWidth, area.Width / textScale);
            expandedWidth = Math.Min(SettingsWidth, area.Width / textScale);
            compactRevealHeight = Math.Min(CompactHeight(), area.Height / textScale);
            expandedRevealHeight = Math.Min(Math.Max(SettingsHeight, compactRevealHeight), area.Height / textScale);

            // A single per-pixel-transparent HWND holds both endpoints. During a
            // transition only the child surface changes: DWM never has to move a
            // previous frame while WPF is still painting its compensating offset.
            double hostWidth = expandedWidth * textScale, hostHeight = expandedRevealHeight * textScale;
            Point mini = new Point(Clamp(origin.X, area.Left, area.Right - compactWidth * textScale),
                Clamp(origin.Y, area.Top, area.Bottom - compactRevealHeight * textScale));
            double left = Clamp(mini.X, area.Left, area.Right - hostWidth);
            double top = Clamp(mini.Y, area.Top, area.Bottom - hostHeight);
            compactOffset = new Vector(mini.X - left, mini.Y - top);
            requestedPosition = null;
            if (Left != left) Left = left;
            if (Top != top) Top = top;
            if (Width != hostWidth) Width = hostWidth;
            if (Height != hostHeight) Height = hostHeight;

            // The same pivot is used for every frame in both directions. Near a screen
            // edge it shifts toward that edge so the surface grows into the work area.
            horizontalPivot = expandedWidth > compactWidth
                ? Clamp(compactOffset.X / (textScale * (expandedWidth - compactWidth)), 0, 1) : 0;
            verticalPivot = expandedRevealHeight > compactRevealHeight
                ? Clamp(compactOffset.Y / (textScale * (expandedRevealHeight - compactRevealHeight)), 0, 1) : 0;
            Details.Width = Math.Max(1, expandedWidth - Inner.Margin.Left - Inner.Margin.Right);

            panelWidth.Aim(Surface.Width, expanded ? expandedWidth : compactWidth, panelAnimating);
            panelHeight.Aim(Surface.Height, expanded ? expandedRevealHeight : compactRevealHeight, panelAnimating);
            if (!animate || PanelSettled())
            {
                FinishPanel();
                return;
            }
            if (!panelAnimating)
            {
                lastFrame = Stopwatch.GetTimestamp();
                lastRenderTime = TimeSpan.MinValue;
                panelAnimating = true;
                CompositionTarget.Rendering += RenderPanelFrame;
            }
        }

        private void RenderPanelFrame(object sender, EventArgs args)
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (time == lastRenderTime) return;
            lastRenderTime = time;
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - lastFrame) / (double)Stopwatch.Frequency;
            lastFrame = now;
            panelWidth.Step(seconds);
            panelHeight.Step(seconds);
            if (PanelSettled()) { FinishPanel(); return; }

            ApplySurface(panelWidth.Position, panelHeight.Position);
            double reveal = Clamp((Surface.Height - compactRevealHeight) /
                Math.Max(1, expandedRevealHeight - compactRevealHeight), 0, 1);
            double fade = Clamp((reveal - 0.20) / 0.66, 0, 1);
            fade = fade * fade * (3 - 2 * fade);
            Details.Opacity = fade;
            ((TranslateTransform)Details.RenderTransform).Y = (1 - fade) * 5;
            MiniRecord.Opacity = 1 - Clamp(reveal / 0.25, 0, 1);
            Rim.SetEnergy(Clamp((Math.Abs(panelWidth.Velocity) + Math.Abs(panelHeight.Velocity)) / 3600, 0, 1));
        }

        private void ApplySurface(double width, double height)
        {
            width = Clamp(width, 1, expandedWidth);
            height = Clamp(height, 1, expandedRevealHeight);
            Surface.Width = width;
            Surface.Height = height;
            var shift = (TranslateTransform)Surface.RenderTransform;
            shift.X = horizontalPivot * (expandedWidth - width);
            shift.Y = verticalPivot * (expandedRevealHeight - height);
        }

        private bool PanelSettled() { return panelWidth.Settled && panelHeight.Settled; }

        private void FinishPanel()
        {
            StopPanelFrames();
            ApplySurface(panelWidth.Target, panelHeight.Target);
            Details.Opacity = expanded ? 1 : 0;
            ((TranslateTransform)Details.RenderTransform).Y = 0;
            Details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            MiniRecord.Visibility = expanded ? Visibility.Hidden : Visibility.Visible;
            MiniRecord.Opacity = expanded ? 0 : 1;
            Rim.SetEnergy(0);
        }

        private void StopPanelFrames()
        {
            if (!panelAnimating) return;
            CompositionTarget.Rendering -= RenderPanelFrame;
            panelAnimating = false;
        }

        private void PanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (panelAnimating && !(bool)e.NewValue) FinishPanel();
        }

        private void PanelUnloaded(object sender, RoutedEventArgs e) { StopPanelFrames(); }
        private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(value, max)); }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { internal int Size; internal NativeRect Monitor, Work; internal uint Flags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private Rect WorkArea(Point anchor)
        {
            var source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null) return SystemParameters.WorkArea;
            Matrix toPixels = source.CompositionTarget.TransformToDevice;
            Matrix fromPixels = source.CompositionTarget.TransformFromDevice;
            Point at = toPixels.Transform(anchor);
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = (int)Math.Round(at.X), Y = (int)Math.Round(at.Y) }, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
            return new Rect(fromPixels.Transform(new Point(info.Work.Left, info.Work.Top)),
                fromPixels.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        }

        private void DisplayChanged(object sender, EventArgs e) { DispatchSystemRefresh(); }
        private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) { DispatchSystemRefresh(); }
        private void SystemPreferenceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "HighContrast" || e.PropertyName == "WorkArea" || e.PropertyName == "MessageFontSize" || e.PropertyName == "ClientAreaAnimation")
                DispatchSystemRefresh();
        }

        private void DispatchSystemRefresh()
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(delegate { ConfigureSystem(); if (IsLoaded) SetGeometry(false); }));
        }

        private void ConfigureSystem()
        {
            double scale = 1;
            try
            {
                object setting = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100);
                if (setting is int) scale = Clamp((int)setting / 100.0, 1, 2.25);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            textScale = scale;
            SyncMotionPreference();
            Rim.InvalidateVisual();
            Stage.LayoutTransform = new ScaleTransform(scale, scale);
            var keys = new[] { "Base", "Raised", "Hover", "Pressed", "Frame", "Line", "Bevel", "Recess", "Ink", "Muted", "OnInk", "Focus" };
            if (standardPalette == null)
            {
                standardPalette = new Dictionary<string, Brush>();
                foreach (string key in keys) standardPalette.Add(key, (Brush)Resources[key]);
            }
            if (!SystemParameters.HighContrast)
            {
                foreach (string key in keys) Resources[key] = standardPalette[key];
                return;
            }
            foreach (string key in new[] { "Base", "Raised", "Recess", "OnInk" }) Resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "Frame", "Line", "Bevel", "Ink", "Muted" }) Resources[key] = SystemColors.WindowTextBrush;
            Resources["Focus"] = SystemColors.HighlightBrush;
            Resources["Hover"] = SystemColors.ControlBrush;
            Resources["Pressed"] = SystemColors.ControlBrush;
        }
    }
}


