using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AutomatiskSportTV.Core.Models;

namespace AutomatiskSportTV.App.Services;

/// <summary>
/// Drives smooth 60 FPS OLED-safe screensaver animations on a pure black canvas:
/// 1) SportBounce: classic DVD-style bouncing neon MatchFlow TV sports emblem with sparks and corner-hit celebrations.
/// 2) StadiumBoard: subtle sweeping arena floodlight halos behind a live match countdown scoreboard.
/// 3) Starfield: relaxing deep-black parallax stars and drifting colored sports orbs.
/// </summary>
public sealed class ScreensaverAnimator
{
    private sealed class SparkParticle
    {
        public required Ellipse Shape { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public double Life { get; set; }
        public double MaxLife { get; set; }
    }

    private sealed class StarParticle
    {
        public required FrameworkElement Element { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double SpeedX { get; set; }
        public double SpeedY { get; set; }
        public double Phase { get; set; }
        public double BaseOpacity { get; set; }
    }

    private static readonly Color[] NeonPalette =
    [
        Color.FromRgb(34, 197, 94),   // MatchFlow Green
        Color.FromRgb(56, 189, 248),  // Electric Sky
        Color.FromRgb(244, 63, 94),   // Viaplay Rose
        Color.FromRgb(245, 158, 11),  // Championship Gold
        Color.FromRgb(168, 85, 247),  // Arena Purple
        Color.FromRgb(239, 68, 68),   // TV4 Red
        Color.FromRgb(6, 182, 212)    // Ice Cyan
    ];

    private static readonly string[] SportIcons = ["⚽", "🏒", "🏎️", "🎾", "🏀", "⛷️", "🤾", "⛳"];

    private readonly Canvas _canvas;
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private readonly List<SparkParticle> _sparks = [];
    private readonly List<StarParticle> _stars = [];

    private Border? _bounceBadge;
    private TextBlock? _bounceIconText;
    private TextBlock? _bounceSubtitleText;
    private DropShadowEffect? _bounceGlow;
    private Ellipse? _floodlightA;
    private Ellipse? _floodlightB;

    private ScreensaverStyle _style = ScreensaverStyle.SportBounce;
    private bool _running;
    private bool _pureBlack;
    private double _lastTimestampSec;
    private double _logoX = 140;
    private double _logoY = 120;
    private double _logoVx = 175;
    private double _logoVy = 135;
    private int _colorIndex;
    private int _iconIndex;
    private int _bounceCount;
    private int _cornerHits;
    private double _cornerFlashSeconds;
    private double _spotlightTime;

    public ScreensaverAnimator(Canvas effectsCanvas)
    {
        _canvas = effectsCanvas;
    }

    public int BounceCount => _bounceCount;
    public int CornerHits => _cornerHits;
    public Color CurrentAccentColor => NeonPalette[_colorIndex % NeonPalette.Length];

    public event Action<int, int, Color>? BounceStatsChanged;

    public void Start(ScreensaverStyle style, bool pureBlackMode)
    {
        _style = style;
        _pureBlack = pureBlackMode;
        RebuildScene();

        if (!_running)
        {
            _running = true;
            _clock.Restart();
            _lastTimestampSec = 0;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public void Stop()
    {
        if (_running)
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
        }
        _canvas.Children.Clear();
        _sparks.Clear();
        _stars.Clear();
        _bounceBadge = null;
        _floodlightA = null;
        _floodlightB = null;
    }

    public void SwitchStyle(ScreensaverStyle newStyle, bool pureBlackMode)
    {
        _style = newStyle;
        _pureBlack = pureBlackMode;
        if (_running)
        {
            RebuildScene();
        }
    }

    private void RebuildScene()
    {
        _canvas.Children.Clear();
        _sparks.Clear();
        _stars.Clear();
        _bounceBadge = null;
        _floodlightA = null;
        _floodlightB = null;

        if (_pureBlack)
        {
            return;
        }

        var width = Math.Max(960, _canvas.ActualWidth);
        var height = Math.Max(540, _canvas.ActualHeight);

        switch (_style)
        {
            case ScreensaverStyle.SportBounce:
                BuildSportBounceScene(width, height);
                break;

            case ScreensaverStyle.StadiumBoard:
                BuildStadiumScene(width, height);
                break;

            case ScreensaverStyle.Starfield:
                BuildStarfieldScene(width, height);
                break;
        }
    }

    private void BuildSportBounceScene(double width, double height)
    {
        // Pre-allocate spark particle pool (zero GC allocations during animation)
        for (var i = 0; i < 28; i++)
        {
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Opacity = 0,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(dot);
            _sparks.Add(new SparkParticle { Shape = dot });
        }

        _bounceGlow = new DropShadowEffect
        {
            Color = CurrentAccentColor,
            BlurRadius = 26,
            ShadowDepth = 0,
            Opacity = 0.85
        };

        _bounceIconText = new TextBlock
        {
            Text = SportIcons[_iconIndex % SportIcons.Length],
            FontSize = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var titleText = new TextBlock
        {
            Text = "MATCHFLOW TV",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.ExtraBold
        };

        _bounceSubtitleText = new TextBlock
        {
            Text = "LIVE SPORT-TV",
            Foreground = new SolidColorBrush(CurrentAccentColor),
            FontSize = 11,
            FontWeight = FontWeights.Bold
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(titleText);
        textStack.Children.Add(_bounceSubtitleText);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(_bounceIconText);
        row.Children.Add(textStack);

        _bounceBadge = new Border
        {
            Width = 224,
            Height = 74,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(225, 9, 14, 26)),
            BorderBrush = new SolidColorBrush(CurrentAccentColor),
            BorderThickness = new Thickness(2.5),
            Padding = new Thickness(16, 10, 16, 10),
            Effect = _bounceGlow,
            Child = row,
            IsHitTestVisible = false
        };

        _logoX = Math.Clamp(_logoX, 20, Math.Max(40, width - _bounceBadge.Width - 20));
        _logoY = Math.Clamp(_logoY, 20, Math.Max(40, height - _bounceBadge.Height - 20));
        Canvas.SetLeft(_bounceBadge, _logoX);
        Canvas.SetTop(_bounceBadge, _logoY);
        _canvas.Children.Add(_bounceBadge);
    }

    private void BuildStadiumScene(double width, double height)
    {
        _floodlightA = CreateFloodlight(Color.FromRgb(34, 197, 94), 560);
        _floodlightB = CreateFloodlight(Color.FromRgb(56, 189, 248), 520);
        _canvas.Children.Add(_floodlightA);
        _canvas.Children.Add(_floodlightB);
        Canvas.SetLeft(_floodlightA, width * 0.15);
        Canvas.SetTop(_floodlightA, height * 0.15);
        Canvas.SetLeft(_floodlightB, width * 0.65);
        Canvas.SetTop(_floodlightB, height * 0.45);

        // Subtle drifting stadium particles (fireflies / lens dust)
        for (var i = 0; i < 18; i++)
        {
            var size = _rng.Next(3, 7);
            var color = NeonPalette[i % NeonPalette.Length];
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
                IsHitTestVisible = false
            };
            var x = _rng.NextDouble() * width;
            var y = _rng.NextDouble() * height;
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            _canvas.Children.Add(dot);

            _stars.Add(new StarParticle
            {
                Element = dot,
                X = x,
                Y = y,
                SpeedX = (_rng.NextDouble() - 0.5) * 18,
                SpeedY = -8 - _rng.NextDouble() * 16,
                Phase = _rng.NextDouble() * Math.PI * 2,
                BaseOpacity = 0.25 + _rng.NextDouble() * 0.35
            });
        }
    }

    private void BuildStarfieldScene(double width, double height)
    {
        for (var i = 0; i < 34; i++)
        {
            FrameworkElement element;
            double speed;
            double baseOpacity;

            if (i < 7)
            {
                // Drifting softly glowing sport icons in deep space
                element = new TextBlock
                {
                    Text = SportIcons[i % SportIcons.Length],
                    FontSize = _rng.Next(18, 28),
                    Opacity = 0.28,
                    IsHitTestVisible = false
                };
                speed = 16 + _rng.NextDouble() * 22;
                baseOpacity = 0.22 + _rng.NextDouble() * 0.20;
            }
            else
            {
                var size = _rng.Next(2, 6);
                var c = i % 3 == 0 ? NeonPalette[i % NeonPalette.Length] : Color.FromRgb(226, 232, 240);
                element = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new SolidColorBrush(c),
                    IsHitTestVisible = false
                };
                speed = 12 + _rng.NextDouble() * 48;
                baseOpacity = 0.25 + _rng.NextDouble() * 0.6;
            }

            var x = _rng.NextDouble() * width;
            var y = _rng.NextDouble() * height;
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _canvas.Children.Add(element);

            _stars.Add(new StarParticle
            {
                Element = element,
                X = x,
                Y = y,
                SpeedX = -speed,
                SpeedY = (_rng.NextDouble() - 0.5) * 8,
                Phase = _rng.NextDouble() * Math.PI * 2,
                BaseOpacity = baseOpacity
            });
        }
    }

    private static Ellipse CreateFloodlight(Color color, double diameter)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(46, color.R, color.G, color.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(14, color.R, color.G, color.B), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));

        return new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = brush,
            IsHitTestVisible = false
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_running || _pureBlack)
        {
            return;
        }

        var nowSec = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(nowSec - _lastTimestampSec, 0.001, 0.05);
        _lastTimestampSec = nowSec;

        var width = _canvas.ActualWidth;
        var height = _canvas.ActualHeight;
        if (width < 200 || height < 200)
        {
            return;
        }

        switch (_style)
        {
            case ScreensaverStyle.SportBounce:
                UpdateSportBounce(dt, width, height);
                break;

            case ScreensaverStyle.StadiumBoard:
                UpdateStadium(dt, width, height);
                break;

            case ScreensaverStyle.Starfield:
                UpdateStarfield(dt, width, height);
                break;
        }
    }

    private void UpdateSportBounce(double dt, double width, double height)
    {
        if (_bounceBadge is null)
        {
            return;
        }

        var maxX = Math.Max(10, width - _bounceBadge.Width);
        var maxY = Math.Max(10, height - _bounceBadge.Height);

        _logoX += _logoVx * dt;
        _logoY += _logoVy * dt;

        var hitHorizontal = false;
        var hitVertical = false;
        double impactX = _logoX + _bounceBadge.Width / 2;
        double impactY = _logoY + _bounceBadge.Height / 2;

        if (_logoX <= 0)
        {
            _logoX = 0;
            _logoVx = Math.Abs(_logoVx);
            hitHorizontal = true;
            impactX = 4;
        }
        else if (_logoX >= maxX)
        {
            _logoX = maxX;
            _logoVx = -Math.Abs(_logoVx);
            hitHorizontal = true;
            impactX = width - 4;
        }

        if (_logoY <= 0)
        {
            _logoY = 0;
            _logoVy = Math.Abs(_logoVy);
            hitVertical = true;
            impactY = 4;
        }
        else if (_logoY >= maxY)
        {
            _logoY = maxY;
            _logoVy = -Math.Abs(_logoVy);
            hitVertical = true;
            impactY = height - 4;
        }

        if (hitHorizontal || hitVertical)
        {
            _bounceCount++;
            _colorIndex = (_colorIndex + 1) % NeonPalette.Length;
            _iconIndex = (_iconIndex + 1) % SportIcons.Length;

            // Detect genuine corner hits (either simultaneous bounce or within 28px of the perpendicular edge)
            var nearVerticalEdge = _logoY <= 28 || _logoY >= maxY - 28;
            var nearHorizontalEdge = _logoX <= 28 || _logoX >= maxX - 28;
            var isCornerHit = (hitHorizontal && nearVerticalEdge) || (hitVertical && nearHorizontalEdge);

            var color = CurrentAccentColor;
            ApplyBounceTheme(color, isCornerHit);

            if (isCornerHit)
            {
                _cornerHits++;
                _cornerFlashSeconds = 2.6;
                SpawnSparks(impactX, impactY, count: 24, color, celebratory: true);
            }
            else
            {
                SpawnSparks(impactX, impactY, count: 8, color, celebratory: false);
            }

            BounceStatsChanged?.Invoke(_bounceCount, _cornerHits, color);
        }

        if (_cornerFlashSeconds > 0)
        {
            _cornerFlashSeconds -= dt;
            if (_cornerFlashSeconds <= 0 && _bounceSubtitleText is not null)
            {
                _bounceSubtitleText.Text = "LIVE SPORT-TV";
                _bounceSubtitleText.Foreground = new SolidColorBrush(CurrentAccentColor);
            }
        }

        Canvas.SetLeft(_bounceBadge, _logoX);
        Canvas.SetTop(_bounceBadge, _logoY);

        UpdateSparks(dt);
    }

    private void ApplyBounceTheme(Color color, bool isCornerHit)
    {
        if (_bounceBadge is null || _bounceGlow is null || _bounceIconText is null || _bounceSubtitleText is null)
        {
            return;
        }

        var borderColor = isCornerHit ? Color.FromRgb(250, 204, 21) : color;
        _bounceBadge.BorderBrush = new SolidColorBrush(borderColor);
        _bounceGlow.Color = borderColor;
        _bounceGlow.BlurRadius = isCornerHit ? 40 : 26;
        _bounceIconText.Text = isCornerHit ? "🏆" : SportIcons[_iconIndex % SportIcons.Length];
        _bounceSubtitleText.Text = isCornerHit ? "🎯 HÖRNTRÄFF!" : "LIVE SPORT-TV";
        _bounceSubtitleText.Foreground = new SolidColorBrush(borderColor);
    }

    private void SpawnSparks(double originX, double originY, int count, Color color, bool celebratory)
    {
        var spawned = 0;
        for (var i = 0; i < _sparks.Count && spawned < count; i++)
        {
            var p = _sparks[i];
            if (p.Life > 0 && !celebratory)
            {
                continue;
            }

            var angle = _rng.NextDouble() * Math.PI * 2;
            var speed = celebratory
                ? 110 + _rng.NextDouble() * 240
                : 65 + _rng.NextDouble() * 145;
            var sparkColor = celebratory ? NeonPalette[_rng.Next(NeonPalette.Length)] : color;

            p.X = originX;
            p.Y = originY;
            p.Vx = Math.Cos(angle) * speed;
            p.Vy = Math.Sin(angle) * speed;
            p.MaxLife = celebratory ? 0.95 + _rng.NextDouble() * 0.45 : 0.45 + _rng.NextDouble() * 0.25;
            p.Life = p.MaxLife;
            p.Shape.Width = celebratory ? 9 : 6;
            p.Shape.Height = celebratory ? 9 : 6;
            p.Shape.Fill = new SolidColorBrush(sparkColor);
            p.Shape.Opacity = 1.0;
            Canvas.SetLeft(p.Shape, p.X);
            Canvas.SetTop(p.Shape, p.Y);
            spawned++;
        }
    }

    private void UpdateSparks(double dt)
    {
        for (var i = 0; i < _sparks.Count; i++)
        {
            var p = _sparks[i];
            if (p.Life <= 0)
            {
                continue;
            }

            p.Life -= dt;
            if (p.Life <= 0)
            {
                p.Shape.Opacity = 0;
                continue;
            }

            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.Vx *= 0.96;
            p.Vy *= 0.96;
            p.Shape.Opacity = Math.Clamp(p.Life / p.MaxLife, 0, 1);
            Canvas.SetLeft(p.Shape, p.X);
            Canvas.SetTop(p.Shape, p.Y);
        }
    }

    private void UpdateStadium(double dt, double width, double height)
    {
        _spotlightTime += dt;

        if (_floodlightA is not null)
        {
            var ax = width * 0.5 + Math.Cos(_spotlightTime * 0.28) * (width * 0.34) - _floodlightA.Width / 2;
            var ay = height * 0.5 + Math.Sin(_spotlightTime * 0.21) * (height * 0.28) - _floodlightA.Height / 2;
            Canvas.SetLeft(_floodlightA, ax);
            Canvas.SetTop(_floodlightA, ay);
        }

        if (_floodlightB is not null)
        {
            var bx = width * 0.5 + Math.Sin(_spotlightTime * 0.23 + 1.8) * (width * 0.32) - _floodlightB.Width / 2;
            var by = height * 0.5 + Math.Cos(_spotlightTime * 0.19 + 0.9) * (height * 0.26) - _floodlightB.Height / 2;
            Canvas.SetLeft(_floodlightB, bx);
            Canvas.SetTop(_floodlightB, by);
        }

        UpdateStarParticles(dt, width, height);
    }

    private void UpdateStarfield(double dt, double width, double height)
    {
        _spotlightTime += dt;
        UpdateStarParticles(dt, width, height);
    }

    private void UpdateStarParticles(double dt, double width, double height)
    {
        for (var i = 0; i < _stars.Count; i++)
        {
            var s = _stars[i];
            s.X += s.SpeedX * dt;
            s.Y += s.SpeedY * dt;

            if (s.X < -40) s.X = width + 20;
            else if (s.X > width + 40) s.X = -20;

            if (s.Y < -40) s.Y = height + 20;
            else if (s.Y > height + 40) s.Y = -20;

            s.Element.Opacity = Math.Clamp(s.BaseOpacity + Math.Sin(_spotlightTime * 1.4 + s.Phase) * 0.15, 0.08, 0.95);
            Canvas.SetLeft(s.Element, s.X);
            Canvas.SetTop(s.Element, s.Y);
        }
    }
}
