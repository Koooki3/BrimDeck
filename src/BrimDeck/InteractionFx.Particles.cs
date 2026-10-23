using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The few particles a press throws over the open panel, in the theme's style (ThemeMotion.Particles): soft dust, small squares as
// on a contribution graph, short sparks, four-point stars that twinkle, fireflies that drift up and glow slowly, or neon motes in
// two colors with a soft glow. They lie under the content, on the surface's material (InteractionFx.Island), so text and controls
// stay above them as above the theme's own gradient and scan lines; each lives well under two seconds and fades, shrinks or turns
// as its style does. A fixed set of shapes is reused; the frame callback runs only while any is alive, and nothing is allocated
// per frame.
internal sealed partial class FxSurface
{
    private const int MoteCount = 32;
    private readonly Canvas _particleLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Mote[] _motes = new Mote[MoteCount];
    private readonly Shape?[] _moteShapes = new Shape?[MoteCount];
    private readonly MatrixTransform[] _moteTransforms = new MatrixTransform[MoteCount];
    private readonly Random _random = new();
    private ParticleFx _moteStyle = ParticleFx.None;
    private Brush? _moteFirst, _moteSecond;
    private bool _motesHooked;
    private double _moteFrame;
    private int _nextMote;
    // A four-point star in a unit square, its points pulled in by curves.
    private static readonly Geometry StarShape = Frozen(Geometry.Parse("M 0.5,0 C 0.56,0.38 0.62,0.44 1,0.5 C 0.62,0.56 0.56,0.62 0.5,1 C 0.44,0.62 0.38,0.56 0,0.5 C 0.38,0.44 0.44,0.38 0.5,0 Z"));

    private struct Mote
    {
        public bool Alive, Second;
        public double X, Y, Vx, Vy, Born, Life, Width, Height, Angle, Spin, Seed;
    }

    // Placed under the content by InteractionFx.Island.
    public UIElement Backdrop => _particleLayer;

    private static Geometry Frozen(Geometry geometry) { geometry.Freeze(); return geometry; }

    // The theme's two colors, as the style draws them: glowing motes get a soft round light, the others a flat color.
    private void DressParticles(Color first, Color second)
    {
        ClearParticles();
        bool soft = _motion.Particles is ParticleFx.Dust or ParticleFx.Firefly or ParticleFx.Neon;
        _moteFirst = soft ? Glow(first) : Solid(first);
        _moteSecond = soft ? Glow(second) : Solid(second);
        _moteStyle = ParticleFx.None;
    }

    private static Brush Glow(Color color)
    {
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(color, 0), new GradientStop(Alpha(color, .55), .4), new GradientStop(Alpha(color, 0), 1)
        });
        brush.Freeze();
        return brush;
    }

    private void EnsureShapes()
    {
        if (_moteStyle == _motion.Particles) return;
        _particleLayer.Children.Clear();
        _moteStyle = _motion.Particles;
        for (int i = 0; i < MoteCount; i++)
        {
            Shape shape = _moteStyle switch
            {
                ParticleFx.Dust or ParticleFx.Firefly or ParticleFx.Neon => new Ellipse(),
                ParticleFx.Star => new Path { Data = StarShape, Stretch = Stretch.Fill },
                ParticleFx.Pixel => new Rectangle { RadiusX = 1, RadiusY = 1 },
                _ => new Rectangle { RadiusX = .8, RadiusY = .8 }
            };
            shape.IsHitTestVisible = false; shape.Visibility = Visibility.Collapsed;
            shape.RenderTransform = _moteTransforms[i] ??= new MatrixTransform();
            _particleLayer.Children.Add(shape);
            _moteShapes[i] = shape;
        }
    }

    // A press throws a small ring of the theme's particles outward from the pointer.
    private void Burst(Point at)
    {
        EnsureShapes();
        const int count = 7;
        double start = _random.NextDouble() * Math.PI * 2;
        for (int i = 0; i < count; i++)
        {
            double angle = start + i * Math.PI * 2 / count + Random(-.2, .2), speed = Random(45, 80);
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            Spawn(at, -direction);
            ref var m = ref _motes[(_nextMote + MoteCount - 1) % MoteCount];
            m.Vx = direction.X * speed; m.Vy = direction.Y * speed - (_moteStyle == ParticleFx.Firefly ? 15 : 0);
            if (_moteStyle == ParticleFx.Spark) m.Angle = angle * 180 / Math.PI;
        }
        HookMotes();
    }

    private void HookMotes()
    {
        if (_motesHooked) return;
        _motesHooked = true; _moteFrame = Eased.Now;
        CompositionTarget.Rendering += MoteFrame;
    }

    private double Random(double from, double to) => from + (to - from) * _random.NextDouble();

    private void Spawn(Point at, Vector direction)
    {
        int index = _nextMote;
        _nextMote = (index + 1) % MoteCount;
        var m = new Mote { Alive = true, X = at.X, Y = at.Y, Born = Eased.Now, Seed = _random.NextDouble() };
        switch (_moteStyle)
        {
            case ParticleFx.Pixel:
                m.Life = .9; m.Width = m.Height = Math.Round(Random(3.5, 5));
                m.Vx = Random(-6, 6); m.Vy = Random(2, 9); m.Second = _random.NextDouble() < .65;
                break;
            case ParticleFx.Spark:
            {
                m.Life = .45; m.Width = Random(4.5, 7.5); m.Height = 2;
                double angle = Math.Atan2(-direction.Y, -direction.X) + Random(-.35, .35), speed = Random(50, 85);
                m.Vx = Math.Cos(angle) * speed; m.Vy = Math.Sin(angle) * speed; m.Angle = angle * 180 / Math.PI;
                m.Second = _random.NextDouble() < .35;
                break;
            }
            case ParticleFx.Star:
                m.Life = .95; m.Width = m.Height = Random(5, 10);
                m.Vx = Random(-10, 10); m.Vy = Random(-10, 10); m.Spin = Random(-120, 120); m.Second = _random.NextDouble() < .45;
                break;
            case ParticleFx.Firefly:
                m.Life = 1.5; m.Width = m.Height = Random(6, 10);
                m.Vx = Random(-8, 8); m.Vy = -Random(14, 28); m.Second = _random.NextDouble() < .75;
                break;
            case ParticleFx.Neon:
                // Round neon motes that follow the pointer's path, alternating the theme's two colors.
                m.Life = .65; m.Width = m.Height = Random(5, 9);
                m.Vx = -direction.X * 18 + Random(-8, 8); m.Vy = -direction.Y * 18 + Random(-8, 8); m.Second = index % 2 == 1;
                break;
            default:
                m.Life = .7; m.Width = m.Height = Random(3, 6);
                m.Vx = -direction.X * 14 + Random(-16, 16); m.Vy = -direction.Y * 14 + Random(-16, 16); m.Second = _random.NextDouble() < .4;
                break;
        }
        // The animation length chosen in settings and the theme's pace stretch or shorten every life alike.
        m.Life *= Math.Clamp((Settings?.AnimationDuration ?? 360) / 360d, .6, 1.6) * Math.Clamp(_motion.Pace, MotionSafety.PaceMin, MotionSafety.PaceMax);
        _motes[index] = m;
        if (_moteShapes[index] is { } shape)
        {
            shape.Width = m.Width; shape.Height = m.Height;
            shape.Fill = m.Second ? _moteSecond : _moteFirst;
            shape.Opacity = 0;
            shape.Visibility = Visibility.Visible;
        }
    }

    private void MoteFrame(object? sender, EventArgs e)
    {
        double now = Eased.Now, dt = Math.Clamp(now - _moteFrame, 0, .05);
        _moteFrame = now;
        double strength = _motion.Trail;
        int alive = 0;
        for (int i = 0; i < MoteCount; i++)
        {
            ref var m = ref _motes[i];
            if (!m.Alive || _moteShapes[i] is not { } shape) continue;
            double age = now - m.Born, t = age / m.Life;
            if (t >= 1) { m.Alive = false; shape.Visibility = Visibility.Collapsed; continue; }
            alive++;
            double drag = Math.Exp(-dt * (_moteStyle == ParticleFx.Spark ? 5 : 2.2));
            m.Vx *= drag; m.Vy *= drag;
            if (_moteStyle == ParticleFx.Pixel) m.Vy += 18 * dt;
            m.X += m.Vx * dt; m.Y += m.Vy * dt;
            // Rises within its first eighth, then fades out softly; never a blink.
            double rise = Math.Min(1, t / .12), alpha = strength * rise * Math.Pow(1 - t, 1.6);
            double sx = 1, sy = 1, x = m.X, y = m.Y, angle = m.Angle;
            switch (_moteStyle)
            {
                case ParticleFx.Dust:
                    sx = sy = 1 - .45 * t;
                    break;
                case ParticleFx.Pixel:
                    alpha = strength * rise * (t < .7 ? 1 : (1 - t) / .3);
                    break;
                case ParticleFx.Spark:
                    sx = 1 - .6 * t;
                    break;
                case ParticleFx.Star:
                    sx = sy = .5 + .6 * Math.Sin(Math.PI * Math.Min(1, t * 1.1));
                    angle += m.Spin * age;
                    break;
                case ParticleFx.Firefly:
                    // A slow glow (under one cycle a second, a third of the light) and a gentle sway as it rises.
                    alpha *= .65 + .35 * Math.Sin(2 * Math.PI * .9 * age + m.Seed * 6.28);
                    x += Math.Sin(age * 2.4 + m.Seed * 6) * 3;
                    break;
                case ParticleFx.Neon:
                    sx = sy = 1 - .55 * t;
                    break;
            }
            shape.Opacity = alpha;
            var matrix = Matrix.Identity;
            matrix.Translate(-m.Width / 2, -m.Height / 2);
            matrix.Scale(sx, sy);
            if (angle != 0) matrix.Rotate(angle);
            matrix.Translate(x, y);
            _moteTransforms[i].Matrix = matrix;
        }
        if (alive == 0) UnhookMotes();
    }

    private void UnhookMotes()
    {
        if (!_motesHooked) return;
        _motesHooked = false;
        CompositionTarget.Rendering -= MoteFrame;
    }

    // Every particle gone at once (a theme change, the window closing).
    private void ClearParticles()
    {
        UnhookMotes();
        for (int i = 0; i < MoteCount; i++)
        {
            _motes[i].Alive = false;
            if (_moteShapes[i] is { } shape) shape.Visibility = Visibility.Collapsed;
        }
    }
}
