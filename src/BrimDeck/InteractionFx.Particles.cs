using System.Windows;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The few particles a completed press leaves (ThemeMotion.Particles), from the control's own edges, never from the pointer:
// stars that twinkle once on its corners (Dracula), fireflies that rise slowly from its top edge and glow (Night Owl), and for
// custom themes dust, pixels, sparks or neon motes. They lie on the layer above the content, small and faint, and live under
// about a second and a half. Their paths are worked out from their age, not stepped frame by frame, so they look the same at
// any frame rate and in a preview; a fixed set is reused and nothing is allocated per frame.
internal sealed partial class FxSurface
{
    private const int MoteCount = 16;
    private readonly Mote[] _motes = new Mote[MoteCount];
    private int _nextMote;
    private double _lastBurst = double.NegativeInfinity;
    private Brush _moteFirst = Brushes.Transparent, _moteSecond = Brushes.Transparent, _moteFirstGlow = Brushes.Transparent, _moteSecondGlow = Brushes.Transparent;
    private Pen _moteFirstPen = new(), _moteSecondPen = new();

    private struct Mote
    {
        public bool Alive, Second;
        public double X, Y, Vx, Vy, Born, Life, Size, Angle, Seed;
    }

    // The first color is the highlight, the second the theme's second tone.
    private void DressParticles(Color main, Color second, Color high)
    {
        ClearParticles();
        _moteFirst = Solid(high); _moteSecond = Solid(second);
        _moteFirstGlow = Radial(high, new Point(.5, .5), .5, .5); _moteSecondGlow = Radial(second, new Point(.5, .5), .5, .5);
        _moteFirstPen = Pen(_moteFirst, 1.1, PenLineCap.Round); _moteSecondPen = Pen(_moteSecond, 1.1, PenLineCap.Round);
    }

    // A few particles from the pressed control's shape, placed by the press's serial so the same press always looks the same.
    private void Burst(FxMark mark, double now)
    {
        var r = mark.Box;
        // Rapid clicks leave one set of particles a third of a second, not a cloud.
        if (r.IsEmpty || now - _lastBurst < .33) return;
        _lastBurst = now;
        uint seed = (uint)mark.Serial * 2654435761u;
        double Next() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return (seed & 0xFFFFFF) / (double)0x1000000; }
        double scale = Math.Clamp((double)AnimationDuration / AnimationLength.Default, .6, 1.6) * Math.Clamp(_motion.Pace, MotionSafety.PaceMin, MotionSafety.PaceMax);
        switch (_motion.Particles)
        {
            case ParticleFx.Star:
                // On three of the corners, just outside the shape; they swell, turn a little and shrink in place.
                Spawn(r.Right - 1, r.Top - 1, 4, -6, .8, 7 + 2 * Next(), false);
                Spawn(r.Left + 1, r.Bottom + 1, -4, 6, .8, 5 + 2 * Next(), true);
                Spawn(r.Left + r.Width * (.3 + .2 * Next()), r.Top - 2, 0, -8, .8, 4.5 + 1.5 * Next(), false);
                break;
            case ParticleFx.Firefly:
                // From the top edge, rising slowly and swaying.
                for (int i = 0; i < 3; i++)
                    Spawn(r.Left + r.Width * (.22 + .28 * i + .08 * (Next() - .5)), r.Top + 2, 5 * (Next() - .5), -(16 + 10 * Next()), 1.25, 3.4 + 1.4 * Next(), i != 1);
                break;
            case ParticleFx.Dust:
                for (int i = 0; i < 4; i++)
                {
                    double angle = (i + .5) * Math.PI / 2 + .3 * (Next() - .5);
                    Spawn(r.X + r.Width / 2 + Math.Cos(angle) * r.Width / 2, r.Y + r.Height / 2 + Math.Sin(angle) * r.Height / 2, Math.Cos(angle) * 12, Math.Sin(angle) * 12, .65, 2 + Next(), i % 2 == 1);
                }
                break;
            case ParticleFx.Pixel:
                for (int i = 0; i < 3; i++) Spawn(r.Left + r.Width * (.25 + .25 * i), r.Bottom - 1, 3 * (Next() - .5), 8 + 6 * Next(), .7, 3, i == 1);
                break;
            case ParticleFx.Spark:
                Spawn(r.Left - 1, r.Y + r.Height * (.35 + .3 * Next()), -40, 0, .35, 6, false);
                Spawn(r.Right + 1, r.Y + r.Height * (.35 + .3 * Next()), 40, 0, .35, 6, true);
                break;
            case ParticleFx.Neon:
                Spawn(r.Left, r.Top, -10, -10, .5, 4, false); Spawn(r.Right, r.Top, 10, -10, .5, 4, true);
                Spawn(r.Left, r.Bottom, -10, 10, .5, 4, true); Spawn(r.Right, r.Bottom, 10, 10, .5, 4, false);
                break;
        }

        void Spawn(double x, double y, double vx, double vy, double life, double size, bool second)
        {
            ref var m = ref _motes[_nextMote];
            _nextMote = (_nextMote + 1) % MoteCount;
            m = new Mote { Alive = true, X = x, Y = y, Vx = vx, Vy = vy, Born = now, Life = life * scale, Size = size, Second = second, Seed = Next(), Angle = 40 * (Next() - .5) };
        }
    }

    // Whether any particle is still alive.
    private bool StepParticles(double now)
    {
        bool any = false;
        for (int i = 0; i < MoteCount; i++)
        {
            ref var m = ref _motes[i];
            if (!m.Alive) continue;
            if (now - m.Born >= m.Life || now < m.Born) m.Alive = false; else any = true;
        }
        return any;
    }

    private void DrawParticles(DrawingContext dc, double now)
    {
        double strength = _motion.ParticleStrength;
        for (int i = 0; i < MoteCount; i++)
        {
            ref var m = ref _motes[i];
            if (!m.Alive) continue;
            double age = now - m.Born, t = Math.Clamp(age / m.Life, 0, 1);
            // Slowed by the air: the distance covered levels off.
            double travel = (1 - Math.Exp(-2.4 * age)) / 2.4;
            double x = m.X + m.Vx * travel, y = m.Y + m.Vy * travel;
            // Rises within its first eighth, then fades softly; never a blink.
            double alpha = strength * Math.Min(1, t / .12) * Math.Pow(1 - t, 1.4);
            var first = !m.Second;
            switch (_motion.Particles)
            {
                case ParticleFx.Star:
                {
                    // One swell and shrink over its life, turning a little: a single twinkle, not a blink.
                    double size = m.Size * Math.Sin(Math.PI * t) * .5;
                    double angle = (m.Angle + 30 * t) * Math.PI / 180, cos = Math.Cos(angle) * size, sin = Math.Sin(angle) * size;
                    double a = strength * Math.Sin(Math.PI * t) * 1.6;
                    if (a <= .001 || size <= .2) break;
                    dc.PushOpacity(Math.Min(1, a));
                    var pen = first ? _moteFirstPen : _moteSecondPen;
                    dc.DrawLine(pen, new Point(x - cos, y - sin), new Point(x + cos, y + sin));
                    dc.DrawLine(pen, new Point(x + sin * .7, y - cos * .7), new Point(x - sin * .7, y + cos * .7));
                    dc.DrawEllipse(first ? _moteFirstGlow : _moteSecondGlow, null, new Point(x, y), size * .7, size * .7);
                    dc.Pop();
                    break;
                }
                case ParticleFx.Firefly:
                {
                    // A slow glow (one swell over its life) and a gentle sway as it rises.
                    x += Math.Sin(age * 3.1 + m.Seed * 6.28) * 2.2;
                    double a = strength * Math.Min(1, t / .2) * (.55 + .45 * Math.Sin(Math.PI * t)) * (1 - t * t) * 1.4;
                    Glow(dc, first, x, y, m.Size, a);
                    break;
                }
                case ParticleFx.Pixel:
                    y += 10 * age * age;
                    if (alpha > .001) { dc.PushOpacity(Math.Min(1, alpha * 1.4)); dc.DrawRectangle(first ? _moteFirst : _moteSecond, null, new Rect(Px(x - m.Size / 2), Px(y - m.Size / 2), Px(m.Size), Px(m.Size))); dc.Pop(); }
                    break;
                case ParticleFx.Spark:
                {
                    double length = m.Size * (1 - .6 * t), dir = Math.Sign(m.Vx);
                    if (alpha > .001) { dc.PushOpacity(Math.Min(1, alpha * 1.4)); dc.DrawLine(first ? _moteFirstPen : _moteSecondPen, new Point(x, y), new Point(x - dir * length, y)); dc.Pop(); }
                    break;
                }
                default:
                    Glow(dc, first, x, y, m.Size * (1 - .4 * t), alpha * 1.3);
                    break;
            }
        }
    }

    private void Glow(DrawingContext dc, bool first, double x, double y, double size, double alpha)
    {
        if (alpha <= .001) return;
        dc.PushOpacity(Math.Min(1, alpha));
        dc.DrawEllipse(first ? _moteFirstGlow : _moteSecondGlow, null, new Point(x, y), size, size);
        dc.DrawEllipse(first ? _moteFirst : _moteSecond, null, new Point(x, y), size * .28, size * .28);
        dc.Pop();
    }

    // Every particle gone at once (a theme change, the window closing).
    private void ClearParticles()
    {
        for (int i = 0; i < MoteCount; i++) _motes[i].Alive = false;
        _nextMote = 0; _lastBurst = double.NegativeInfinity;
    }
}
