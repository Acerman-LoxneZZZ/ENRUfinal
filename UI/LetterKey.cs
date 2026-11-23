using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Raskladka
{
    public sealed class LetterKey : Grid
    {
        private readonly Border top;
        private readonly LetterMorph letters;
        private readonly Rectangle shine;
        private bool russianBase, hovered, suppressPreview, motionEnabled = true;

        public bool MotionEnabled
        {
            get { return motionEnabled; }
            set
            {
                motionEnabled = value;
                if (!CanAnimate)
                {
                    letters.Show(PreviewRussian, false, false);
                    Motion.To(shine, OpacityProperty, 0, 0);
                }
            }
        }

        private bool PreviewRussian { get { return russianBase ^ (hovered && !suppressPreview); } }
        private bool CanAnimate { get { return motionEnabled && IsVisible && !SystemParameters.HighContrast; } }

        public LetterKey()
        {
            AutomationProperties.SetName(this, "Клавиша G / П");
            Background = Brushes.Transparent;
            Children.Add(Frame("Recess", "Line"));
            top = Frame("Hover", "Frame");
            top.Margin = new Thickness(0, 0, 0, 3);
            top.RenderTransform = new TranslateTransform();
            Children.Add(top);

            var face = new Grid { ClipToBounds = true };
            top.Child = face;
            var lip = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 0, 3, 0) };
            lip.SetResourceReference(Border.BackgroundProperty, "Bevel");
            face.Children.Add(lip);
            letters = new LetterMorph { IsHitTestVisible = false };
            face.Children.Add(letters);
            shine = new Rectangle { Width = 12, Height = 1, Opacity = 0, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform(), IsHitTestVisible = false };
            shine.SetResourceReference(Shape.FillProperty, "Ink");
            face.Children.Add(shine);

            MouseEnter += delegate { SetHover(true); };
            MouseLeave += delegate { SetHover(false); };
            IsVisibleChanged += delegate { if (!IsVisible) ResetHover(); };
            Unloaded += delegate { ResetHover(); };
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new LetterKeyPeer(this);
        }

        private sealed class LetterKeyPeer : FrameworkElementAutomationPeer
        {
            internal LetterKeyPeer(LetterKey owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Image; }
            protected override string GetClassNameCore() { return "LetterKey"; }
        }

        private static Border Frame(string surface, string edge)
        {
            var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, surface);
            border.SetResourceReference(Border.BorderBrushProperty, edge);
            return border;
        }

        private void SetHover(bool inside)
        {
            if (inside == hovered) return;
            hovered = inside;
            suppressPreview = false;
            letters.Show(PreviewRussian, CanAnimate, false);
        }

        private void ResetHover()
        {
            hovered = suppressPreview = false;
            letters.Show(russianBase, false, false);
        }

        internal void Press(bool pressed, bool animate)
        {
            Motion.To(top.RenderTransform, TranslateTransform.YProperty, pressed ? 1.5 : 0,
                animate && CanAnimate ? (pressed ? Motion.Press : Motion.Release) : 0);
        }

        internal void Confirm(bool toRussian, bool animate)
        {
            russianBase = toRussian;
            suppressPreview = hovered;
            bool play = animate && CanAnimate;
            letters.Show(russianBase, play, true);
            Motion.To(shine.RenderTransform, TranslateTransform.XProperty, 42, play ? 170 : 0, -12, null, null, 60);
            Motion.To(shine, OpacityProperty, 0, play ? 170 : 0, play ? 0.85 : 0, null, null, 60);
        }
    }
}
