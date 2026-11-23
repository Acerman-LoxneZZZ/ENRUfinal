using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Raskladka
{
    internal static class Motion
    {
        // Unit-mass damped spring. Step velocity first, as in DesignBookmark's motion.js.
        internal static void StepSpring(ref double position, ref double velocity, double target,
            double stiffness, double damping, double dt)
        {
            double acceleration = stiffness * (target - position) - damping * velocity;
            velocity += acceleration * dt;
            position += velocity * dt;
        }

        internal const int Enter = 160, Press = 70, Release = 100, Record = 140, Roll = 240;
        internal const int Switch = 128, Status = 120;
        internal static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, AnimationClock>> Clocks
            = new ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, AnimationClock>>();

        // Snapshot the current animated value before replacing its clock, including on reversal.
        internal static void To(DependencyObject target, DependencyProperty property, double value, int milliseconds,
            double? from = null, IEasingFunction easing = null, Action complete = null, int delay = 0)
        {
            var animated = (IAnimatable)target;
            double current = from ?? (double)target.GetValue(property);
            Cancel(target, property);
            if (milliseconds <= 0 || Math.Abs(current - value) < 0.0001)
            {
                target.SetValue(property, value);
                if (complete != null) complete();
                return;
            }
            // WPF can be idle between interactions. Keep the true starting frame as the
            // base until the first fresh rendering tick creates the animation's time origin.
            target.SetValue(property, current);
            var curve = easing ?? Ease;
            var animation = new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(delay + milliseconds))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = delay == 0 ? curve : new DelayedEase(delay / (double)(delay + milliseconds), curve)
            };
            var clock = (AnimationClock)animation.CreateClock(true);
            var active = Clocks.GetOrCreateValue(target);
            active[property] = clock;
            clock.Completed += delegate
            {
                AnimationClock latest;
                if (!active.TryGetValue(property, out latest) || !ReferenceEquals(clock, latest)) return;
                active.Remove(property);
                target.SetValue(property, value);
                animated.ApplyAnimationClock(property, null);
                clock.Controller.Remove();
                if (complete != null) complete();
            };
            animated.ApplyAnimationClock(property, clock);
        }

        internal static void Cancel(DependencyObject target, DependencyProperty property)
        {
            Dictionary<DependencyProperty, AnimationClock> active;
            AnimationClock previous;
            if (Clocks.TryGetValue(target, out active) && active.TryGetValue(property, out previous))
            {
                active.Remove(property);
                previous.Controller.Remove();
            }
            ((IAnimatable)target).ApplyAnimationClock(property, null);
        }

        // A single clock holds the current value during the delay and then advances the curve.
        private sealed class DelayedEase : IEasingFunction
        {
            private readonly double hold;
            private readonly IEasingFunction curve;
            internal DelayedEase(double hold, IEasingFunction curve) { this.hold = hold; this.curve = curve; }
            public double Ease(double time) { return time <= hold ? 0 : curve.Ease((time - hold) / (1 - hold)); }
        }

    }
}
