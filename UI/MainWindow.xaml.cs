using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Raskladka
{
    public partial class MainWindow : Window
    {
        public Func<uint, uint, string> RegisterBinding { get; set; }
        public Action<int, bool, bool, bool, bool, int> PreferencesChanged { get; set; }
        public Action<bool> RecordingChanged { get; set; }
        public Action HideRequested { get; set; }
        public bool IsRecording { get; private set; }

        private readonly DispatcherTimer statusTimer;
        private readonly CheckBox[] switches;
        private HotkeyBinding binding, candidate;
        private bool expanded, loading = true, persistentError;
        private int mode;
        private Button recorder;
        private bool Animate { get { return mode == 1 || (mode == 0 && SystemParameters.ClientAreaAnimation); } }

        public MainWindow()
        {
            InitializeComponent();
            switches = new[] { RecentSwitch, LayoutSwitch, TopmostSwitch, NotifySwitch };
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            statusTimer.Tick += delegate { statusTimer.Stop(); if (!IsRecording && !persistentError) SetStatus("Готово к работе"); };
            PreviewKeyDown += OnKeyDown;
            PreviewKeyUp += OnKeyUp;
            Deactivated += delegate { if (IsRecording) EndRecording(); };
            IsVisibleChanged += delegate { if (!IsVisible && IsRecording) EndRecording(); };
            Loaded += WindowLoaded;
            Closed += delegate
            {
                statusTimer.Stop();
                SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            };
            SourceInitialized += delegate { ConfigureSystem(); };
            SetPreferences(0, false, false, false, false, 0);
            SetHotkey(0, 0);
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
            ConfigureSystem();
            SetGeometry(false);
            foreach (var item in switches) MoveSwitch(item, false);
            if (Animate)
            {
                Inner.RenderTransform = new TranslateTransform();
                Motion.To(Inner.RenderTransform, TranslateTransform.YProperty, 0, Motion.Enter, 4);
                Motion.To(Inner, OpacityProperty, 1, Motion.Enter, 0);
            }
        }

        public void SetPreferences(int direction, bool recent, bool switchLayout, bool topmost, bool notifications, int motion)
        {
            loading = true;
            mode = Math.Max(0, Math.Min(motion, 2));
            SyncMotionPreference();
            new[] { AutoDirection, ToRuDirection, ToEnDirection }[Math.Max(0, Math.Min(direction, 2))].IsChecked = true;
            new[] { SystemMotion, FullMotion, MinimalMotion }[mode].IsChecked = true;
            RecentSwitch.IsChecked = recent;
            LayoutSwitch.IsChecked = switchLayout;
            TopmostSwitch.IsChecked = Topmost = topmost;
            NotifySwitch.IsChecked = notifications;
            RecentExample.Visibility = recent ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in switches) MoveSwitch(item, false);
            loading = false;
        }

        private void ChangePreference(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            if (IsRecording) EndRecording();
            ClearError();
            mode = FullMotion.IsChecked == true ? 1 : MinimalMotion.IsChecked == true ? 2 : 0;
            SyncMotionPreference();
            Topmost = TopmostSwitch.IsChecked == true;
            RecentExample.Visibility = RecentSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            var toggle = sender as CheckBox;
            if (toggle != null) MoveSwitch(toggle, Animate);
            SavePreferences();
        }

        private void SyncMotionPreference()
        {
            Signature.MotionEnabled = Animate && !SystemParameters.HighContrast;
            if (!Animate && panelAnimating) SetGeometry(false);
        }

        private void SavePreferences()
        {
            var changed = PreferencesChanged;
            if (changed != null) changed(ToRuDirection.IsChecked == true ? 1 : ToEnDirection.IsChecked == true ? 2 : 0,
                RecentSwitch.IsChecked == true, LayoutSwitch.IsChecked == true, TopmostSwitch.IsChecked == true,
                NotifySwitch.IsChecked == true, mode);
        }

        private static void MoveSwitch(CheckBox toggle, bool animate)
        {
            toggle.ApplyTemplate();
            if (toggle.Template == null) return;
            var thumb = toggle.Template.FindName("Thumb", toggle) as Ellipse;
            var fill = toggle.Template.FindName("CheckedTrack", toggle) as Border;
            if (fill != null) Motion.To(fill, OpacityProperty, toggle.IsChecked == true ? 1 : 0, animate ? Motion.Switch + 24 : 0);
            if (thumb != null)
            {
                if (thumb.RenderTransform.IsFrozen) thumb.RenderTransform = thumb.RenderTransform.Clone();
                Motion.To(thumb.RenderTransform, TranslateTransform.XProperty, toggle.IsChecked == true ? 16 : 0, animate ? Motion.Switch : 0);
            }
        }

        public void SetHotkey(uint modifiers, uint virtualKey)
        {
            binding = virtualKey == 0 ? null : new HotkeyBinding((int)modifiers, (int)virtualKey);
            RefreshKeys();
            RefreshPanelMeasure();
        }

        private void RefreshKeys()
        {
            double previousWidth = recorder == null ? 0 : recorder.ActualWidth;
            var display = IsRecording ? candidate : binding;
            string name = IsRecording ? "Записать сочетание" : binding == null ? "Назначить сочетание" : "Изменить сочетание " + binding;
            foreach (var button in new[] { MiniRecord, SettingsRecord })
            {
                AutomationProperties.SetName(button, name);
                button.ToolTip = IsRecording ? null : name;
            }
            DrawCaps(MiniKeys, display);
            DrawCaps(SettingsKeys, display);
            if (IsRecording && Animate && previousWidth > 0)
            {
                // Measure the new caps at their natural width, then move just the recording control.
                Motion.Cancel(recorder, WidthProperty);
                recorder.ClearValue(WidthProperty);
                recorder.Measure(new Size(recorder.MaxWidth, double.PositiveInfinity));
                double width = Math.Min(recorder.MaxWidth, recorder.DesiredSize.Width);
                Motion.To(recorder, WidthProperty, width, Motion.Record, previousWidth);
            }
        }

        private void DrawCaps(WrapPanel panel, HotkeyBinding value)
        {
            panel.Children.Clear();
            if (value == null || (value.Modifiers == 0 && value.VirtualKey == 0))
            {
                panel.Children.Add(new TextBlock { Text = IsRecording ? "Нажмите клавиши" : "Назначить сочетание",
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                return;
            }
            foreach (string key in value.GetKeycaps())
            {
                var cap = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(1, 1, 1, 1), BorderThickness = new Thickness(1, 1, 1, 2) };
                cap.SetResourceReference(Border.BackgroundProperty, "Raised");
                cap.SetResourceReference(Border.BorderBrushProperty, IsRecording ? "Bevel" : "Frame");
                cap.Child = new TextBlock { Text = key, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                panel.Children.Add(cap);
            }
        }

        private void BeginRecording(object sender, RoutedEventArgs e)
        {
            if (IsRecording) return;
            ClearError();
            statusTimer.Stop();
            recorder = (Button)sender;
            candidate = null;
            IsRecording = true;
            if (RecordingChanged != null) RecordingChanged(true);
            recorder.Focus();
            SetStatus("Запись сочетания");
            ShowHint("Enter — сохранить, Esc — отменить");
            RefreshKeys();
            Signature.Press(true, Animate);
        }

        private void EndRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            if (RecordingChanged != null) RecordingChanged(false);
            persistentError = false;
            Hint.Visibility = Visibility.Collapsed;
            ClearRecordOutline();
            Motion.Cancel(recorder, WidthProperty);
            recorder.ClearValue(WidthProperty);
            SetStatus("Готово к работе");
            RefreshKeys();
            Signature.Press(false, Animate);
            SizeToSummary();
        }

        public void CaptureActiveHotkey(uint modifiers, uint virtualKey)
        {
            if (!IsRecording) return;
            SetCandidate(new HotkeyBinding((int)modifiers, (int)virtualKey));
        }

        private void SetCandidate(HotkeyBinding key)
        {
            persistentError = false;
            ClearRecordOutline();
            candidate = key;
            RefreshKeys();
            if (key.VirtualKey != 0 && !key.IsValid)
            {
                ShowHint("Выберите функциональную клавишу или сочетание с модификатором");
                recorder.SetResourceReference(BorderBrushProperty, "Focus");
            }
            else ShowHint("Enter — сохранить, Esc — отменить");
            Signature.Press(true, Animate);
        }

        private void ConfirmCandidate()
        {
            if (candidate == null || !candidate.IsValid)
            {
                ShowHint("Выберите функциональную клавишу или сочетание с модификатором");
                return;
            }
            string error = RegisterBinding == null ? "Не удалось назначить сочетание" :
                RegisterBinding((uint)candidate.Modifiers, (uint)candidate.VirtualKey);
            if (!string.IsNullOrEmpty(error))
            {
                ShowHint(error);
                persistentError = true;
                recorder.SetResourceReference(BorderBrushProperty, "Focus");
                return;
            }
            SetHotkey((uint)candidate.Modifiers, (uint)candidate.VirtualKey);
            EndRecording();
        }

        private static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsRecording)
            {
                e.Handled = true;
                if (e.IsRepeat) return;
                if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { EndRecording(); return; }
                if (key == Key.Return && Keyboard.Modifiers == ModifierKeys.None) { ConfirmCandidate(); return; }
                int vk = IsModifier(key) ? 0 : KeyInterop.VirtualKeyFromKey(key);
                SetCandidate(new HotkeyBinding((int)Keyboard.Modifiers, vk));
                return;
            }
            if (!expanded) return;
            if (key == Key.Escape)
            {
                ShowMini();
                SettingsButton.Focus();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            switch (key)
            {
                case Key.PageDown: SettingsScroll.PageDown(); break;
                case Key.PageUp: SettingsScroll.PageUp(); break;
                case Key.Home: SettingsScroll.ScrollToTop(); break;
                case Key.End: SettingsScroll.ScrollToBottom(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (!IsRecording) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            // Some registered combinations deliver only their release to the foreground recorder.
            if (!IsModifier(key) && key != Key.Return && key != Key.Escape && (candidate == null || candidate.VirtualKey == 0))
                SetCandidate(new HotkeyBinding((int)Keyboard.Modifiers, KeyInterop.VirtualKeyFromKey(key)));
            e.Handled = true;
            Signature.Press(false, Animate);
        }

        public void ShowSuccess(bool toRussian)
        {
            if (IsRecording) return;
            ClearError();
            SetStatus("Исправлено · " + (toRussian ? "EN → RU" : "RU → EN"));
            Signature.Confirm(toRussian, Animate && IsVisible);
            statusTimer.Stop();
            statusTimer.Start();
        }

        public void ShowError(string message)
        {
            statusTimer.Stop();
            persistentError = true;
            ShowHint(message);
            HintFrame.BorderThickness = new Thickness(2, 0, 0, 0);
            HintFrame.Padding = new Thickness(8, 0, 0, 0);
            SetStatus("Текст не изменён");
            SizeToSummary();
        }

        private void ClearRecordOutline()
        {
            MiniRecord.ClearValue(BorderBrushProperty);
            SettingsRecord.ClearValue(BorderBrushProperty);
        }

        private void ClearError()
        {
            if (!persistentError) return;
            persistentError = false;
            ClearRecordOutline();
            HintFrame.ClearValue(Border.BorderThicknessProperty);
            HintFrame.ClearValue(Border.PaddingProperty);
            Hint.Visibility = Visibility.Collapsed;
            SetStatus("Готово к работе");
            SizeToSummary();
        }

        private void SetStatus(string value)
        {
            Status.Text = value;
            RefreshPanelMeasure();
            if (Animate && IsVisible) Motion.To(Status, OpacityProperty, 1, Motion.Status, 0.6);
            Announce(Status);
        }

        private void ShowHint(string message)
        {
            Hint.Text = message;
            Hint.Visibility = Visibility.Visible;
            Announce(Hint);
            SizeToSummary();
        }

        private static void Announce(TextBlock item)
        {
            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void OpenOrCloseSettings(object sender, RoutedEventArgs e)
        {
            if (expanded) ShowMini(); else ShowSettings();
        }

        public void ShowSettings()
        {
            if (IsRecording) EndRecording();
            ClearError();
            if (!expanded) { ChangeExpansion(true); }
            if (!IsVisible) Show();
        }

        public void ShowMini()
        {
            if (IsRecording) EndRecording();
            ClearError();
            if (expanded) ChangeExpansion(false);
        }

        private void HideToTray(object sender, RoutedEventArgs e)
        {
            if (IsRecording) EndRecording();
            if (HideRequested != null) HideRequested(); else Hide();
        }

    }
}
