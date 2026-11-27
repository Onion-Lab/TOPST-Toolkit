using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace TopstToolkit.App;

/// <summary>
/// The start-up animation (design-system §6, docs/mockups/mockup-intro.html — the first seven seconds of the topst.ai
/// film, made 3.2 s): dark → a white bloom opens into the TOPST periwinkle → flat squares gather and rise into voxel
/// clusters with "Begin Bold, Build Beyond." → the clusters flatten and rise again with "Only For Device ?" → the scene
/// fades into the app. A click or a key skips to the fade. <see cref="ShowFrame"/> sets every moment from the time alone,
/// with the mockup's own numbers, so screen captures can stop at any moment.
/// </summary>
public partial class IntroView : UserControl
{
    public const double Total = 3.2;
    public const double FadeStart = 2.65;
    private const double Unit = 40;                 // one voxel, px at the scene's depth
    private const double Perspective = 1500;        // the mockup's CSS perspective, px

    private sealed record ClusterSpec(string Voxels, double X, double Y, double Size, double Delay);

    /// <summary>The mockup's clusters: voxels (x right, y down, z toward the viewer), resting place, size, delay.</summary>
    private static readonly ClusterSpec[] Specs =
    [
        new("0,0,0;1,0,0;0,1,0;1,1,0;0,0,1;1,0,1;0,1,1", -190, -205, 0.85, 0.00),
        new("0,0,0;1,0,0;2,0,0;0,1,0;1,1,0;2,1,0;0,2,0;1,2,0;0,0,1;1,0,1;2,0,1;0,1,1;1,1,1;0,0,2;1,0,2;1,1,2;2,2,1;2,1,2", 305, -40, 1.05, 0.06),
        new("0,0,0;1,0,0;1,1,0;2,1,0;1,2,0;0,1,1;1,1,1;1,0,1;1,1,2;0,0,1", -345, 120, 0.95, 0.03),
        new("0,0,0;1,0,0;0,1,0;1,1,0;0,0,1;1,0,1;2,1,0;1,2,0;1,1,1;0,1,1", -30, 215, 0.9, 0.09),
        new("0,0,0", 62, -96, 0.55, 0.12),
        new("0,0,0;1,0,0", 160, 140, 0.5, 0.15),
    ];

    /// <summary>The flat squares that gather first: from (x, y) × 1.9 to (x, y) × 0.28, side in px.</summary>
    private static readonly (double X, double Y, double Side)[] TileSpecs =
        [(-420, -260, 120), (380, -230, 90), (-300, 230, 100), (330, 210, 110), (-40, -300, 80), (40, 300, 70), (-520, 20, 70), (520, 40, 80)];

    private sealed class Cluster
    {
        public required ClusterSpec Spec { get; init; }
        public required ScaleTransform3D Scale { get; init; }
        public required AxisAngleRotation3D RotX { get; init; }
        public required AxisAngleRotation3D RotY { get; init; }
        public required AxisAngleRotation3D RotZ { get; init; }
        public required TranslateTransform3D Move { get; init; }
        public required IReadOnlyList<Brush> Brushes { get; init; }
    }

    private sealed class Tile
    {
        public required Border Element { get; init; }
        public required TranslateTransform Move { get; init; }
        public required RotateTransform Turn { get; init; }
        public required ScaleTransform Size { get; init; }
        public required (double X, double Y) From { get; init; }
        public required (double X, double Y) To { get; init; }
    }

    private readonly List<Cluster> _clusters = [];
    private readonly List<Tile> _tiles = [];
    private readonly Stopwatch _clock = new();
    private double _skipped;
    private bool _playing;
    private double _anchorY;

    /// <summary>Raised when the animation has ended (or was never needed); the view is collapsed by then.</summary>
    public event EventHandler? Finished;

    public IntroView()
    {
        InitializeComponent();
        BuildTiles();
        BuildClusters();
        SizeChanged += (_, _) => FitCamera();
        MouseLeftButtonDown += (_, e) =>
        {
            Skip();
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            // Alt (+F4 and the system menu) stays the window's
            if (e.Key is Key.System or Key.LeftAlt or Key.RightAlt)
                return;
            Skip();
            e.Handled = true;
        };
        ShowFrame(0);
    }

    /// <summary>Plays it from the start, about 3.2 s, then collapses itself.</summary>
    public void Play()
    {
        Visibility = Visibility.Visible;
        _skipped = 0;
        _clock.Reset();             // starts with the first frame on screen (OnRendering), not while the window still loads
        if (!_playing)
        {
            _playing = true;
            CompositionTarget.Rendering += OnRendering;
        }
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>Straight to the fade into the app.</summary>
    public void Skip()
    {
        var t = _clock.Elapsed.TotalSeconds + _skipped;
        if (_playing && _clock.IsRunning && t < FadeStart)
            _skipped += FadeStart - t;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_clock.IsRunning)
            _clock.Start();
        var t = _clock.Elapsed.TotalSeconds + _skipped;
        ShowFrame(Math.Min(t, Total));
        if (t >= Total)
            Stop();
    }

    private void Stop()
    {
        if (_playing)
        {
            _playing = false;
            CompositionTarget.Rendering -= OnRendering;
        }
        _clock.Stop();
        Visibility = Visibility.Collapsed;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ building the scene
    private void BuildTiles()
    {
        for (var i = 0; i < TileSpecs.Length; i++)
        {
            var (x, y, side) = TileSpecs[i];
            var move = new TranslateTransform();
            var turn = new RotateTransform();
            var size = new ScaleTransform();
            var gradient = new LinearGradientBrush { StartPoint = new Point(0.41, 0), EndPoint = new Point(0.59, 1) };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xEB, 0x62, 0x7C, 0xFF), 0));     // rgba(98,124,255,.92)
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xC7, 0x54, 0x96, 0xFF), 1));     // rgba(84,150,255,.78)
            gradient.Freeze();
            var element = new Border
            {
                Width = side, Height = side, CornerRadius = new CornerRadius(4), Background = gradient,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { size, turn, move } },
                Effect = new DropShadowEffect { Color = Color.FromRgb(0x37, 0x3C, 0xA0), Opacity = 0.18, BlurRadius = 30, ShadowDepth = 10, Direction = 270 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(element, -side / 2);
            Canvas.SetTop(element, -side / 2);
            Tiles.Children.Add(element);
            _tiles.Add(new Tile { Element = element, Move = move, Turn = turn, Size = size, From = (x * 1.9, y * 1.9), To = (x * 0.28, y * 0.28) });
        }
    }

    /// <summary>One face direction: its unit normal in the mockup's axes (y down) and its brush.</summary>
    private static readonly (string Key, int Dx, int Dy, int Dz)[] Faces =
        [("r", 1, 0, 0), ("l", -1, 0, 0), ("t", 0, -1, 0), ("b", 0, 1, 0), ("n", 0, 0, 1), ("k", 0, 0, -1)];

    private static Brush FaceBrush(string key)
    {
        static LinearGradientBrush Gradient(Point start, Point end, params (string Color, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (var (color, offset) in stops)
                brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(color), offset));
            return brush;
        }
        // the mockup's CSS: 135deg = to the bottom right, 165deg = down and a little right, 195deg = down and a little left
        return key switch
        {
            "t" => Gradient(new Point(0, 0), new Point(1, 1), ("#F0BDFF", 0), ("#BBA9FF", 0.55), ("#9FA6FF", 1)),
            "n" => Gradient(new Point(0.37, 0), new Point(0.63, 1), ("#9C9CFF", 0), ("#7C8BFF", 0.55), ("#6B93FF", 1)),
            "r" => Gradient(new Point(0.63, 0), new Point(0.37, 1), ("#86B4FF", 0), ("#6C98FF", 0.6), ("#5C80F2", 1)),
            "l" => Gradient(new Point(0.37, 0), new Point(0.63, 1), ("#B4A2FF", 0), ("#8E8CFF", 0.6), ("#7A82F6", 1)),
            "b" => new SolidColorBrush(Color.FromRgb(0x5D, 0x6B, 0xE6)),
            _ => new SolidColorBrush(Color.FromRgb(0x6E, 0x7C, 0xF2)),
        };
    }

    private void BuildClusters()
    {
        foreach (var spec in Specs)
        {
            var cubes = spec.Voxels.Split(';').Select(v => v.Split(',').Select(int.Parse).ToArray()).ToList();
            var has = cubes.Select(c => (c[0], c[1], c[2])).ToHashSet();
            double cx = cubes.Average(c => c[0]), cy = cubes.Average(c => c[1]), cz = cubes.Average(c => c[2]);
            var group = new Model3DGroup();
            var brushes = new List<Brush>();
            foreach (var (key, dx, dy, dz) in Faces)
            {
                var mesh = new MeshGeometry3D();
                foreach (var c in cubes)
                {
                    if (has.Contains((c[0] + dx, c[1] + dy, c[2] + dz)))
                        continue; // inside the cluster: not drawn
                    // the mockup's axes have y down; WPF's 3D has y up
                    AddFace(mesh, key, (c[0] - cx) * Unit, -(c[1] - cy) * Unit, (c[2] - cz) * Unit);
                }
                if (mesh.Positions.Count == 0)
                    continue;
                mesh.Freeze();
                var brush = FaceBrush(key);
                brushes.Add(brush);
                group.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
            }
            var scale = new ScaleTransform3D();
            var rotX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            var rotY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            var rotZ = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
            var move = new TranslateTransform3D();
            // CSS "translate rotateX rotateY rotateZ scale" = scale first, then Z, Y, X, then the move
            group.Transform = new Transform3DGroup
            {
                Children = { scale, new RotateTransform3D(rotZ), new RotateTransform3D(rotY), new RotateTransform3D(rotX), move },
            };
            World.Children.Add(group);
            _clusters.Add(new Cluster { Spec = spec, Scale = scale, RotX = rotX, RotY = rotY, RotZ = rotZ, Move = move, Brushes = brushes });
        }
    }

    /// <summary>One square of a cube, counter-clockwise from outside, its texture top at the face's top (as the CSS faces).</summary>
    private static void AddFace(MeshGeometry3D mesh, string key, double x, double y, double z)
    {
        var h = Unit / 2;
        Point3D[] corners = key switch
        {
            "n" => [new(x - h, y - h, z + h), new(x + h, y - h, z + h), new(x + h, y + h, z + h), new(x - h, y + h, z + h)],
            "k" => [new(x + h, y - h, z - h), new(x - h, y - h, z - h), new(x - h, y + h, z - h), new(x + h, y + h, z - h)],
            "r" => [new(x + h, y - h, z + h), new(x + h, y - h, z - h), new(x + h, y + h, z - h), new(x + h, y + h, z + h)],
            "l" => [new(x - h, y - h, z - h), new(x - h, y - h, z + h), new(x - h, y + h, z + h), new(x - h, y + h, z - h)],
            "t" => [new(x - h, y + h, z + h), new(x + h, y + h, z + h), new(x + h, y + h, z - h), new(x - h, y + h, z - h)],
            _ => [new(x - h, y - h, z - h), new(x + h, y - h, z - h), new(x + h, y - h, z + h), new(x - h, y - h, z + h)],
        };
        var start = mesh.Positions.Count;
        foreach (var p in corners)
            mesh.Positions.Add(p);
        // bottom-left, bottom-right, top-right, top-left
        mesh.TextureCoordinates.Add(new Point(0, 1));
        mesh.TextureCoordinates.Add(new Point(1, 1));
        mesh.TextureCoordinates.Add(new Point(1, 0));
        mesh.TextureCoordinates.Add(new Point(0, 0));
        foreach (var i in new[] { 0, 1, 2, 0, 2, 3 })
            mesh.TriangleIndices.Add(start + i);
    }

    /// <summary>
    /// One pixel of the scene's depth is one pixel on the screen, whatever the window's size (as CSS perspective), and the
    /// scene's centre is at 46 % of the height while the camera looks at the middle.
    /// </summary>
    private void FitCamera()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;
        Camera.FieldOfView = 2 * Math.Atan(ActualWidth / 2 / Perspective) * 180 / Math.PI;
        _anchorY = (0.5 - 0.46) * ActualHeight;
    }

    // ------------------------------------------------------------------ the moments
    private static double Clamp(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    private static double Seg(double t, double a, double b) => Clamp((t - a) / (b - a));
    private static double OutCubic(double x) => 1 - Math.Pow(1 - x, 3);
    private static double InOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    private static double OutBack(double x)
    {
        const double c1 = 1.5, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
    private static double Lerp(double a, double b, double x) => a + (b - a) * x;

    /// <summary>Everything as it is <paramref name="t"/> seconds after the start (the mockup's frame(t)).</summary>
    public void ShowFrame(double t)
    {
        if (_anchorY == 0)
            FitCamera();
        // dark → bloom → periwinkle
        Dark.Opacity = 1 - OutCubic(Seg(t, 0.18, 0.42));
        var b = Seg(t, 0, 0.5);
        BloomScale.ScaleX = BloomScale.ScaleY = Lerp(0.08, 1.6, OutCubic(b));
        Bloom.Opacity = b < 0.45 ? OutCubic(b / 0.45) : 1 - InOut((b - 0.45) / 0.55);

        // flat squares gather, then give way to the voxels
        for (var i = 0; i < _tiles.Count; i++)
        {
            var q = _tiles[i];
            var g = OutCubic(Seg(t, 0.30 + i * 0.015, 0.75));
            q.Move.X = Lerp(q.From.X, q.To.X, g);
            q.Move.Y = Lerp(q.From.Y, q.To.Y, g);
            q.Turn.Angle = Lerp(-25 + i * 7, 0, g);
            q.Size.ScaleX = q.Size.ScaleY = Lerp(1.3, 0.55, g);
            q.Element.Opacity = Math.Min(Seg(t, 0.30, 0.42), 1 - Seg(t, 0.66, 0.86));
        }

        // voxel clusters
        for (var i = 0; i < _clusters.Count; i++)
        {
            var c = _clusters[i];
            var d = c.Spec.Delay;
            var p = OutBack(Seg(t, 0.62 + d, 1.12 + d));                         // pop out and spread
            var flat = InOut(Seg(t, 1.70, 1.92)) * (1 - InOut(Seg(t, 1.95, 2.28)));
            var turn = InOut(Seg(t, 1.70, 2.30));                                // the second pose turns the other way
            var drift = t - 0.62;
            var rx = Lerp(-28, -24, turn) * (1 - flat) + Math.Sin(drift * 1.3 + i) * 3;
            var ry = Lerp(36 + drift * 9, -34 - drift * 6, turn) * (1 - flat);
            var rz = (1 - p) * (i % 2 == 1 ? 40 : -40);
            var x = Lerp(c.Spec.X * 0.15, c.Spec.X, p);
            var y = Lerp(c.Spec.Y * 0.15, c.Spec.Y, p) + Math.Sin(drift * 1.6 + i * 1.7) * 6;
            var s = c.Spec.Size * Lerp(0.25, 1, Clamp(p));
            var sz = Lerp(1, 0.06, flat) * Lerp(0.05, 1, OutCubic(Seg(t, 0.62 + d, 0.95 + d)));
            // the CSS angles in WPF's axes (y up): about X and Z the turn is the other way
            c.RotX.Angle = -rx;
            c.RotY.Angle = ry;
            c.RotZ.Angle = -rz;
            c.Scale.ScaleX = c.Scale.ScaleY = Math.Max(s, 0.0001);
            c.Scale.ScaleZ = Math.Max(s * sz, 0.0001);
            c.Move.OffsetX = x;
            c.Move.OffsetY = -y + _anchorY;
            var opacity = Seg(t, 0.60 + d, 0.72 + d);
            foreach (var brush in c.Brushes)
                brush.Opacity = opacity;
        }

        // words
        double a = OutCubic(Seg(t, 0.70, 0.98)), w1 = OutCubic(Seg(t, 0.82, 1.10)), swap = InOut(Seg(t, 1.72, 1.98));
        Lead.Opacity = a;
        LeadMove.Y = Lerp(14, 0, a);
        One.Opacity = w1 * (1 - swap);
        OneMove.Y = Lerp(14, 0, w1) - swap * 16;
        Two.Opacity = swap;
        TwoMove.Y = Lerp(16, 0, swap);
        Mark.Opacity = OutCubic(Seg(t, 0.95, 1.25));

        // out into the app
        var o = InOut(Seg(t, FadeStart, Total));
        Root.Opacity = 1 - o;
        SceneScale.ScaleX = SceneScale.ScaleY = Lerp(1, 1.06, o);
    }
}
