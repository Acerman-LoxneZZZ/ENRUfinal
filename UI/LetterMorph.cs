using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Raskladka
{
    // One surface owns both the font outlines and the material that moves between them.
    internal sealed class LetterMorph : FrameworkElement
    {
        private const double WidthUnits = 38, HeightUnits = 35;
        private const double Stiffness = 72, Damping = 11;
        private const double Sweep = 0.39, Flight = 1 - Sweep;
        private const double SweepX = 0.74, SweepY = 1 - SweepX;
        private const double UnsealStart = 0.035, UnsealEnd = 0.16;
        private const double WeldStart = 1 - UnsealEnd, WeldEnd = 1 - UnsealStart;
        private const double TravelSeconds = 0.84;
        private const int Tints = 10, Shades = 32;
        private static readonly DependencyProperty InkProperty = DependencyProperty.Register("Ink",
            typeof(Brush), typeof(LetterMorph), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty MutedProperty = DependencyProperty.Register("Muted",
            typeof(Brush), typeof(LetterMorph), new FrameworkPropertyMetadata(Brushes.Silver, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly Typeface Typeface = new Typeface(new FontFamily("Cascadia Mono, Consolas"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private readonly Legend english = new Legend("G"), russian = new Legend("П");
        private readonly Dot[] dots;
        private readonly SolidColorBrush[,] colors = new SolidColorBrush[Tints, Shades];
        private Color inkColor, mutedColor;
        private enum Phase { Still, Particles, Roll }
        private Phase phase;
        private bool rollTarget, rollSource;
        private long lastTick;
        private double rollTime, command, progress, velocity;

        internal bool IsAnimating { get { return phase != Phase.Still; } }
        internal bool RussianPrimary { get; private set; }

        private sealed class Legend
        {
            internal readonly Geometry Large, Small;
            internal Legend(string label)
            {
                Large = Outline(label, 29, 5, 4, false);
                Small = Outline(label, 13, 33, 31, true);
            }

            private static Geometry Outline(string label, int size, double x, double y, bool corner)
            {
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Typeface, size, Brushes.White, 1.0);
                var glyph = text.BuildGeometry(new Point());
                Rect bounds = glyph.Bounds;
                glyph.Transform = new TranslateTransform(x - (corner ? bounds.Right : bounds.Left),
                    y - (corner ? bounds.Bottom : bounds.Top));
                glyph.Freeze();
                return glyph;
            }
        }

        private struct Dot
        {
            internal Point Start, End;
            internal double Order, Latitude, Longitude, Ring, LargeRadius, SmallRadius;
            internal bool English;
        }

        internal LetterMorph()
        {
            SetResourceReference(InkProperty, "Ink");
            SetResourceReference(MutedProperty, "Muted");
            var material = new List<Dot>();
            Seed(english, true, material);
            Seed(russian, false, material);
            dots = material.ToArray();

            // Shuffle sphere sites, not glyph cells. The same speck belongs to the same
            // piece of type on every frame and follows the same path in both directions.
            var sites = new int[dots.Length];
            var random = new Random(319);
            for (int i = 0; i < sites.Length; i++) sites[i] = i;
            for (int i = sites.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1), swap = sites[i];
                sites[i] = sites[j]; sites[j] = swap;
            }
            for (int i = 0; i < dots.Length; i++)
            {
                double latitude = 1 - 2.0 * (sites[i] + 0.5) / dots.Length;
                dots[i].Latitude = latitude;
                dots[i].Ring = Math.Sqrt(1 - latitude * latitude);
                dots[i].Longitude = sites[i] * Math.PI * (3 - Math.Sqrt(5));
            }
            Unloaded += delegate { Snap(); };
        }

        private static void Seed(Legend legend, bool isEnglish, List<Dot> result)
        {
            Rect large = legend.Large.Bounds, small = legend.Small.Bounds;
            // A hexagonal lattice gives each particle a patch of ink with even coverage.
            const double pitch = 0.88;
            double rowHeight = pitch * Math.Sqrt(3) / 2;
            int row = 0;
            for (double y = large.Top + rowHeight / 2; y < large.Bottom; y += rowHeight, row++)
            {
                double startX = large.Left + ((row & 1) == 0 ? 0.25 : 0.75) * pitch;
                for (double x = startX; x < large.Right; x += pitch)
                {
                    var big = new Point(x, y);
                    if (!legend.Large.FillContains(big)) continue;
                    double xn = (x - large.Left) / large.Width, yn = (y - large.Top) / large.Height;
                    var little = new Point(small.Left + xn * small.Width, small.Top + yn * small.Height);
                    result.Add(new Dot {
                        Start = isEnglish ? big : little, End = isEnglish ? little : big,
                        English = isEnglish, Order = xn * SweepX + yn * SweepY,
                        LargeRadius = pitch * 0.59, SmallRadius = pitch * 0.59 * small.Height / large.Height
                    });
                }
            }
        }

        internal void Show(bool russianPrimary, bool animate, bool correction)
        {
            bool previous = RussianPrimary;
            RussianPrimary = russianPrimary;
            if (!animate || !IsVisible) { Snap(); return; }
            if (correction && phase == Phase.Still)
            {
                rollTime = 0;
                rollSource = previous;
                rollTarget = russianPrimary;
                Start(Phase.Roll);
                return;
            }
            if (phase == Phase.Roll || previous == russianPrimary) return;
            // Keep the in-flight position and velocity; only redirect what the spring follows.
            command = Clamp(progress);
            Start(Phase.Particles);
        }

        private void Start(Phase nextPhase)
        {
            if (phase == Phase.Still)
            {
                lastTick = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += Tick;
            }
            phase = nextPhase;
            InvalidateVisual();
        }

        private void SetPosition(bool toRussian)
        {
            progress = command = toRussian ? 1 : 0;
            velocity = 0;
        }

        private void Snap()
        {
            if (phase != Phase.Still) CompositionTarget.Rendering -= Tick;
            phase = Phase.Still;
            SetPosition(RussianPrimary);
            InvalidateVisual();
        }

        private void Tick(object sender, EventArgs e)
        {
            if (!IsVisible) { Snap(); return; }
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Min(1.5, (now - lastTick) / (double)Stopwatch.Frequency);
            lastTick = now;
            if (phase == Phase.Roll)
            {
                rollTime += elapsed;
                if (rollTime >= Motion.Roll / 1000.0)
                {
                    if (RussianPrimary == rollTarget) { Snap(); return; }
                    SetPosition(rollTarget);
                    Start(Phase.Particles);
                }
            }
            else
            {
                double target = RussianPrimary ? 1 : 0;
                int steps = Math.Max(1, (int)Math.Ceiling(elapsed * 120));
                double dt = elapsed / steps;
                for (int i = 0; i < steps; i++)
                {
                    double step = dt / TravelSeconds;
                    command += Math.Max(-step, Math.Min(step, target - command));
                    Motion.StepSpring(ref progress, ref velocity, command, Stiffness, Damping, dt);
                }
                bool arrived = RussianPrimary ? progress >= 1 : progress <= 0;
                if (command == target && (arrived || (Math.Abs(progress - target) < 0.0005 && Math.Abs(velocity) < 0.006)))
                {
                    Snap();
                    return;
                }
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            dc.PushTransform(new ScaleTransform(ActualWidth / WidthUnits, ActualHeight / HeightUnits));
            if (phase == Phase.Roll) DrawRoll(dc);
            else if (phase == Phase.Particles) DrawAssembly(dc);
            else DrawPair(dc, RussianPrimary);
            dc.Pop();
        }

        private void DrawPair(DrawingContext dc, bool toRussian)
        {
            dc.DrawGeometry((Brush)GetValue(InkProperty), null, (toRussian ? russian : english).Large);
            dc.DrawGeometry((Brush)GetValue(MutedProperty), null, (toRussian ? english : russian).Small);
        }

        private void DrawRoll(DrawingContext dc)
        {
            double t = Clamp(rollTime * 1000 / Motion.Roll);
            double eased = 1 - Math.Pow(1 - t, 3);
            int direction = rollTarget ? 1 : -1;
            dc.PushClip(new RectangleGeometry(new Rect(1, 2, 24, 24)));
            dc.PushTransform(new TranslateTransform(0, -24 * direction * eased));
            dc.DrawGeometry((Brush)GetValue(InkProperty), null, (rollSource ? russian : english).Large);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(0, 24 * direction * (1 - eased)));
            dc.DrawGeometry((Brush)GetValue(InkProperty), null, (rollTarget ? russian : english).Large);
            dc.Pop();
            dc.Pop();
            dc.DrawGeometry((Brush)GetValue(MutedProperty), null, (rollTarget ? english : russian).Small);
        }

        private static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }
        private static double Smooth(double value)
        {
            double t = Clamp(value);
            return t * t * (3 - 2 * t);
        }

        private void DrawAssembly(DrawingContext dc)
        {
            UpdatePalette();
            double p = Clamp(progress);
            DrawInk(dc, english.Large, inkColor, p, false);
            DrawInk(dc, russian.Small, mutedColor, p, false);
            DrawInk(dc, russian.Large, inkColor, p, true);
            DrawInk(dc, english.Small, mutedColor, p, true);

            for (int i = 0; i < dots.Length; i++)
            {
                double local = Clamp((p - Sweep * dots[i].Order) / Flight);
                if (local > 0 && local < 1) DrawDot(dc, ref dots[i], local);
            }
        }

        // Lay down the continuous vector edge only behind the particles that have
        // reached their patch of the letter. Ahead of this narrow front there is no ink.
        private static void DrawInk(DrawingContext dc, Geometry shape, Color ink, double p, bool arriving)
        {
            double left = (p - Flight * (arriving ? WeldEnd : UnsealEnd)) / Sweep;
            double right = (p - Flight * (arriving ? WeldStart : UnsealStart)) / Sweep;
            if ((arriving && right <= 0) || (!arriving && left >= 1)) return;
            if ((arriving && left >= 1) || (!arriving && right <= 0))
            {
                var solid = new SolidColorBrush(ink);
                solid.Freeze();
                dc.DrawGeometry(solid, null, shape);
                return;
            }

            Rect box = shape.Bounds;
            // Dot.Order is the same projected coordinate in both sizes of each glyph.
            var normal = new Vector(SweepX / box.Width, SweepY / box.Height);
            Vector axis = normal / normal.LengthSquared;
            var clear = Color.FromArgb(0, ink.R, ink.G, ink.B);
            var brush = new LinearGradientBrush(arriving ? ink : clear, arriving ? clear : ink,
                box.TopLeft + axis * left, box.TopLeft + axis * right) { MappingMode = BrushMappingMode.Absolute };
            brush.Freeze();
            dc.DrawGeometry(brush, null, shape);
        }

        private void DrawDot(DrawingContext dc, ref Dot dot, double local)
        {
            // The speck first emerges at the exact ink sample. It has landed before
            // the welding front arrives, so the strokes assemble continuously.
            double t = Smooth((local - UnsealEnd) / (WeldStart - UnsealEnd));
            double cloud = Math.Sin(Math.PI * t);
            cloud *= cloud;
            double angle = dot.Longitude + t * 2.75;
            double depth = dot.Ring * Math.Sin(angle);
            double perspective = 57 / (57 - depth * 9.5);
            double sphereX = dot.Ring * Math.Cos(angle) * 10.8 * perspective;
            double sphereY = (0.95 * dot.Latitude - 0.31 * depth) * 10.8 * perspective;
            double sweep = Math.Sin(2 * Math.PI * t) * cloud * 0.52;
            double x = dot.Start.X + (dot.End.X - dot.Start.X) * t;
            double y = dot.Start.Y + (dot.End.Y - dot.Start.Y) * t;
            x += cloud * (WidthUnits / 2 + sphereX - x) - sweep * sphereY;
            y += cloud * (HeightUnits / 2 + sphereY - y) + sweep * sphereX;

            double large = dot.English ? 1 - t : t;
            double atLetter = dot.SmallRadius + (dot.LargeRadius - dot.SmallRadius) * large;
            double radius = atLetter + (0.40 + 0.15 * (1 - depth) - atLetter) * cloud;
            double opacity = 1 - cloud * (0.42 - 0.35 * depth);
            // End dots overlap the outline as the final subpixel gaps are filled.
            opacity *= Smooth(Math.Min(local, 1 - local) / 0.045);
            int tint = (int)Math.Round((large + (1 - large) * cloud * 0.65) * (Tints - 1));
            int shade = (int)Math.Round(Clamp(opacity) * (Shades - 1));
            dc.DrawEllipse(colors[tint, shade], null, new Point(x, y), radius, radius);
        }

        private void UpdatePalette()
        {
            Color ink = ((SolidColorBrush)GetValue(InkProperty)).Color;
            Color muted = ((SolidColorBrush)GetValue(MutedProperty)).Color;
            if (colors[0, 0] != null && ink == inkColor && muted == mutedColor) return;
            inkColor = ink;
            mutedColor = muted;
            for (int i = 0; i < Tints; i++)
            {
                double mix = i / (double)(Tints - 1);
                var tone = Color.FromRgb((byte)(muted.R + (ink.R - muted.R) * mix),
                    (byte)(muted.G + (ink.G - muted.G) * mix), (byte)(muted.B + (ink.B - muted.B) * mix));
                for (int j = 0; j < Shades; j++)
                {
                    tone.A = (byte)((muted.A + (ink.A - muted.A) * mix) * j / (Shades - 1));
                    var brush = new SolidColorBrush(tone);
                    brush.Freeze();
                    colors[i, j] = brush;
                }
            }
        }
    }
}
