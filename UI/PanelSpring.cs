using System;
using System.Windows;
using System.Windows.Media;

namespace Raskladka
{
    // Critically damped travel keeps the edge monotonic after each uninterrupted command.
    internal sealed class PanelSpring
    {
        private readonly double frequency;
        internal double Position, Velocity, Target;

        internal PanelSpring(double stiffness)
        {
            frequency = Math.Sqrt(stiffness);
        }

        internal void Aim(double from, double target, bool retainVelocity)
        {
            Position = from;
            Target = target;
            if (!retainVelocity) Velocity = 0;
        }

        internal void Step(double seconds)
        {
            // Exact solution for a critically damped spring of unit mass. A delayed
            // presentation frame advances by its actual elapsed time.
            double displacement = Position - Target;
            double slope = Velocity + frequency * displacement;
            double decay = Math.Exp(-frequency * seconds);
            Position = Target + (displacement + slope * seconds) * decay;
            Velocity = (Velocity - frequency * slope * seconds) * decay;
        }

        internal bool Settled
        {
            get { return Math.Abs(Target - Position) < 0.18 && Math.Abs(Velocity) < 2; }
        }
    }

    // Only the outline is drawn here. The content above it always uses native WPF text.
    public sealed class PanelRim : FrameworkElement
    {
        private static Pen WhiteEdge(byte alpha, double width)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), width);
            pen.Freeze();
            return pen;
        }

        private static readonly Pen Outer = WhiteEdge(11, 7);
        private static readonly Pen Halo = WhiteEdge(18, 4.5);
        private static readonly Pen Bloom = WhiteEdge(30, 2.5);
        private static readonly Pen Edge = MakeEdge();
        private static readonly Pen Highlight = WhiteEdge(116, 1.2);
        private double energy;

        private static Pen MakeEdge()
        {
            var ink = new LinearGradientBrush();
            ink.StartPoint = new Point(0, 0);
            ink.EndPoint = new Point(0.8, 1);
            ink.GradientStops.Add(new GradientStop(Color.FromArgb(177, 255, 255, 255), 0));
            ink.GradientStops.Add(new GradientStop(Color.FromArgb(111, 255, 255, 255), 0.48));
            ink.GradientStops.Add(new GradientStop(Color.FromArgb(139, 255, 255, 255), 1));
            var pen = new Pen(ink, 1);
            pen.Freeze();
            return pen;
        }

        internal void SetEnergy(double value)
        {
            if (Math.Abs(energy - value) < 0.015 && value != 0) return;
            if (energy == value) return;
            energy = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (RenderSize.Width < 9 || RenderSize.Height < 9) return;
            var body = new Rect(4, 4, RenderSize.Width - 8, RenderSize.Height - 8);
            var fill = (Brush)FindResource("Base");
            if (SystemParameters.HighContrast)
            {
                dc.DrawRoundedRectangle(fill, new Pen(SystemColors.WindowTextBrush, 2), body, 14, 14);
                return;
            }
            dc.DrawRoundedRectangle(null, Outer, body, 14, 14);
            dc.DrawRoundedRectangle(null, Halo, body, 14, 14);
            dc.DrawRoundedRectangle(null, Bloom, body, 14, 14);
            dc.DrawRoundedRectangle(fill, Edge, body, 14, 14);
            if (energy <= 0) return;
            dc.PushOpacity(energy);
            dc.DrawRoundedRectangle(null, Highlight, body, 14, 14);
            dc.Pop();
        }
    }
}

