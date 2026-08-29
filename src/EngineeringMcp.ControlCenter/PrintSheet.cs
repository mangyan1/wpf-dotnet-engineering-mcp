using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace EngineeringMcp.ControlCenter;

/// <summary>
/// Builds the engineering print sheet decoration shown in the Print (cyanotype) theme:
/// double-line sheet frame, grid-reference rulers, dimension line, bolt circle, crosshair
/// targets, and the top-center clock gear train (outlined teeth, dash-dot pitch circles,
/// counter-rotating wheels geared to their tooth counts). 1:1 port of the mockup's
/// .printdeco layer + buildGearTrain().
/// </summary>
internal static class PrintSheet
{
    private const string InkBrushKey = "EngineeringDecorationBrush";
    private const string InkFillBrushKey = "EngineeringDecorationFillBrush";
    // mockup renders ink at 0.38 opacity + mix-blend-mode:screen, which measures to a
    // peak stroke of ~(64,89,109) over the sheet. WPF has no screen blend; solving
    // bg + a*(ink-bg) for that output gives a=0.26 — verified pixel-equal.
    private const double InkOpacity = 0.26;
    private const string CenterlineDash = "8 4 2 4";
    private const string PitchDash = "7 3 2 3";
    private const double GearTrainVerticalOffset = -18;
    private const double SecondaryPageGearTrainVerticalOffset = 82;

    private sealed record DecorationState(FrameworkElement Furniture, Action<bool> SetSecondaryPageLayout);

    public static FrameworkElement Build()
    {
        // margin/alignment-positioned sheet furniture...
        var root = new Grid { IsHitTestVisible = false, Opacity = InkOpacity };
        var furniture = new Grid();
        root.Children.Add(furniture);

        furniture.Children.Add(Frame(10));
        furniture.Children.Add(Frame(13));

        // grid-reference rulers: A–H across the top, 1–6 down the side
        var rulerX = new UniformGrid { Rows = 1, Columns = 8, Height = 16, Margin = new Thickness(22, 13, 22, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var c in "ABCDEFGH") rulerX.Children.Add(RulerLabel(c.ToString()));
        furniture.Children.Add(rulerX);

        var rulerY = new UniformGrid { Columns = 1, Rows = 6, Width = 16, Margin = new Thickness(13, 22, 0, 22), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 1; i <= 6; i++) rulerY.Children.Add(RulerLabel(i.ToString()));
        furniture.Children.Add(rulerY);

        // dimension line (bottom-left): 840.0 + 45° angle arc
        var dims = new Canvas
        {
            Width = 220, Height = 110,
            Margin = new Thickness(64, 0, 0, 56),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        dims.Children.Add(InkLine(10, 90, 210, 90, 1));
        dims.Children.Add(InkLine(10, 80, 10, 100, 1));
        dims.Children.Add(InkLine(210, 80, 210, 100, 1));
        dims.Children.Add(Filled("M10 90 l12 -3 v6 z M210 90 l-12 -3 v6 z"));
        dims.Children.Add(InkLine(30, 40, 90, 40, 1));
        dims.Children.Add(InkPath("M30 40 a30 30 0 0 1 21 -9", 1));
        dims.Children.Add(InkText("840.0", 98, 70, 11));
        dims.Children.Add(InkText("45°", 44, 18, 11));
        furniture.Children.Add(dims);

        // bolt circle (top right)
        var bolts = new Canvas
        {
            Width = 150, Height = 150,
            Margin = new Thickness(0, 100, 320, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        bolts.Children.Add(InkEllipse(80, 80, 54, 54, 1, "4 4"));
        bolts.Children.Add(InkEllipse(80, 80, 10, 10, 1));
        double[] bx = [80, 118, 134, 118, 80, 42, 26, 42], by = [26, 42, 80, 118, 134, 118, 80, 42];
        for (var i = 0; i < 8; i++) bolts.Children.Add(InkEllipse(bx[i], by[i], 7, 7, 1));
        bolts.Children.Add(InkLine(80, 4, 80, 14, 1));
        bolts.Children.Add(InkLine(80, 146, 80, 156, 1));
        bolts.Children.Add(InkLine(4, 80, 14, 80, 1));
        bolts.Children.Add(InkLine(146, 80, 156, 80, 1));
        furniture.Children.Add(bolts);

        // Full-size furniture canvas for crosshairs anchored to viewport size/center.
        var furnitureAnchored = new Canvas { ClipToBounds = false };
        furniture.Children.Add(furnitureAnchored);

        // crosshair targets: c1 at 38% width / y 84, c2 at x 120 / 44% height
        var cross1 = Crosshair();
        var cross2 = Crosshair();
        furnitureAnchored.SizeChanged += (_, _) =>
        {
            Canvas.SetLeft(cross1, furnitureAnchored.ActualWidth * 0.38 - 27);
            Canvas.SetTop(cross1, 84);
            Canvas.SetLeft(cross2, 120);
            Canvas.SetTop(cross2, furnitureAnchored.ActualHeight * 0.44 - 27);
        };
        furnitureAnchored.Children.Add(cross1);
        furnitureAnchored.Children.Add(cross2);

        // Gear canvas is independent from print-only furniture so dashboard identity persists in every theme.
        var gearCanvas = new Canvas { ClipToBounds = false };
        root.Children.Add(gearCanvas);
        var gears = new List<(FrameworkElement Wheel, GearSpec Spec, double C)>();
        foreach (var s in GearTrainLayout.Specs)
        {
            var (wheel, c) = GearWheel(s);
            gears.Add((wheel, s, c));
            gearCanvas.Children.Add(wheel);
        }
        var currentGearVerticalOffset = GearTrainVerticalOffset;
        gearCanvas.SizeChanged += (_, _) =>
        {
            foreach (var (wheel, spec, c) in gears)
            {
                Canvas.SetLeft(wheel, gearCanvas.ActualWidth / 2 + spec.X - c);
                Canvas.SetTop(wheel, spec.Y + currentGearVerticalOffset - c);
            }
        };

        root.Tag = new DecorationState(furniture, secondaryPage =>
        {
            currentGearVerticalOffset = secondaryPage
                ? SecondaryPageGearTrainVerticalOffset
                : GearTrainVerticalOffset;

            foreach (var (wheel, spec, c) in gears)
                Canvas.SetTop(wheel, spec.Y + currentGearVerticalOffset - c);
        });

        return root;
    }

    public static void SetSecondaryPageLayout(FrameworkElement sheet, bool secondaryPage)
    {
        if (sheet.Tag is DecorationState state)
            state.SetSecondaryPageLayout(secondaryPage);
    }

    public static void SetThemeMode(FrameworkElement sheet, string themeMode)
    {
        if (sheet.Tag is not DecorationState state) return;

        state.Furniture.Visibility = themeMode == "Print" ? Visibility.Visible : Visibility.Collapsed;
        sheet.Opacity = themeMode switch
        {
            "Light" => 0.28,
            "Dark" => 0.23,
            _ => InkOpacity,
        };
    }

    private static FrameworkElement Frame(double inset)
    {
        var rectangle = new Rectangle
        {
            Margin = new Thickness(inset),
            StrokeThickness = 1
        };
        SetInk(rectangle, Shape.StrokeProperty);
        return rectangle;
    }

    private static TextBlock RulerLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetInk(label, TextBlock.ForegroundProperty);
        return label;
    }

    // ---- one wheel, technical-drawing style: outlined teeth (valley arcs + flanks + tips),
    //      dash-dot pitch circle, double rim, bolt holes, radial spokes, marker spoke ----
    private static (FrameworkElement, double) GearWheel(GearSpec s)
    {
        var tooth = Math.Max(6, s.R * 0.13);
        var rt = s.R + tooth;
        var c = rt + 10;
        var size = c * 2;

        var body = new Canvas { Width = size, Height = size, ClipToBounds = false };

        var step = 2 * Math.PI / s.Teeth;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Teeth; i++)
        {
            var a = i * step;
            // SVG in the mockup uses a viewBox centred on (0,0). WPF Canvas coordinates
            // start at the top-left, so every tooth point must be translated to the arbor.
            // Without this offset the tooth ring orbits around the hub instead of spinning
            // with it, producing the large wandering polygons that used to cross the UI.
            sb.Append(i == 0 ? 'M' : 'L').Append(Pt(c, s.R, a - 0.30 * step));
            sb.Append(" L").Append(Pt(c, rt, a - 0.16 * step));
            sb.Append(" L").Append(Pt(c, rt, a + 0.16 * step));
            sb.Append(" L").Append(Pt(c, s.R, a + 0.30 * step));
            sb.Append(" A").Append(Inv(s.R)).Append(' ').Append(Inv(s.R)).Append(" 0 0 1 ")
              .Append(Pt(c, s.R, (i + 1) * step - 0.30 * step));
        }
        sb.Append(" Z");
        var teeth = new Path
        {
            Data = Geometry.Parse(sb.ToString()),
            StrokeThickness = 1.1 // mockup: .teeth stroke-width 1.1
        };
        SetInk(teeth, Shape.StrokeProperty);
        teeth.SetResourceReference(Shape.FillProperty, InkFillBrushKey);
        body.Children.Add(teeth);

        body.Children.Add(InkEllipse(c, c, s.R + tooth * 0.45, s.R + tooth * 0.45, 0.7, PitchDash));
        body.Children.Add(InkEllipse(c, c, s.R - 3, s.R - 3, 1.2));
        body.Children.Add(InkEllipse(c, c, s.R * 0.55, s.R * 0.55, 1.2));
        if (s.R >= 40) // bolt-hole circle on larger wheels
        {
            for (var i = 0; i < 6; i++)
            {
                var a = i * Math.PI / 3 + Math.PI / 6;
                body.Children.Add(InkEllipse(c + 0.62 * s.R * Math.Cos(a), c + 0.62 * s.R * Math.Sin(a),
                    s.R * 0.05, s.R * 0.05, 1.2));
            }
        }
        var spokes = Math.Max(4, (int)Math.Round(s.R / 20));
        for (var i = 0; i < spokes; i++)
        {
            var a = 2 * Math.PI * i / spokes;
            body.Children.Add(InkLine(c + 0.18 * s.R * Math.Cos(a), c + 0.18 * s.R * Math.Sin(a),
                c + 0.60 * s.R * Math.Cos(a), c + 0.60 * s.R * Math.Sin(a), 1));
        }
        if (s.Centerlines)
        {
            body.Children.Add(InkLine(0, c, 2 * c, c, 0.7, CenterlineDash));
            body.Children.Add(InkLine(c, 0, c, 2 * c, 0.7, CenterlineDash));
        }
        body.Children.Add(InkEllipse(c, c, s.R * 0.14, s.R * 0.14, 1.2));
        body.Children.Add(FilledDot(c, c, 2.2));
        body.Children.Add(InkLine(c, c, c + 0.55 * s.R, c, 1.6));

        // spin around the arbor; direction and period geared to the tooth count.
        // BitmapCache: pre-rasterize once at 2x and rotate the bitmap, like the mockup's
        // GPU-composited CSS transform — without it WPF re-tessellates the thin tooth
        // strokes every frame and they shimmer/crawl against the rims.
        // ponytail: A/B-verified sharper than per-frame vector re-rasterization (midPct 7.9 vs 9.9)
        body.CacheMode = new BitmapCache(2.0);
        var rotate = new RotateTransform(s.PhaseDegrees, c, c);
        body.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = s.PhaseDegrees,
            To = s.PhaseDegrees + (s.Ccw ? -360 : 360),
            Duration = TimeSpan.FromSeconds(s.Seconds),
            RepeatBehavior = RepeatBehavior.Forever
        });

        Canvas.SetTop(body, s.Y + GearTrainVerticalOffset - c);
        return (body, c);
    }

    private static FrameworkElement Crosshair()
    {
        const double size = 54, h = size / 2;
        var g = new Canvas { Width = size, Height = size };
        var ring = new Ellipse { Width = 36, Height = 36, StrokeThickness = 1 };
        SetInk(ring, Shape.StrokeProperty);
        Canvas.SetLeft(ring, h - 18);
        Canvas.SetTop(ring, h - 18);
        g.Children.Add(ring);
        var dot = new Ellipse { Width = 5, Height = 5 };
        SetInk(dot, Shape.FillProperty);
        Canvas.SetLeft(dot, h - 2.5);
        Canvas.SetTop(dot, h - 2.5);
        g.Children.Add(dot);
        g.Children.Add(InkLine(h, 0, h, size, 1));
        g.Children.Add(InkLine(0, h, size, h, 1));
        return g;
    }

    private static Line InkLine(double x1, double y1, double x2, double y2, double thickness, string? dash = null)
    {
        var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = thickness };
        SetInk(l, Shape.StrokeProperty);
        if (dash is not null) l.StrokeDashArray = ParseDashes(dash);
        return l;
    }

    private static Path Filled(string geometry)
    {
        var path = new Path { Data = Geometry.Parse(geometry) };
        SetInk(path, Shape.FillProperty);
        return path;
    }

    private static Path InkPath(string geometry, double thickness)
    {
        var path = new Path { Data = Geometry.Parse(geometry), StrokeThickness = thickness };
        SetInk(path, Shape.StrokeProperty);
        return path;
    }

    private static Ellipse InkEllipse(double cx, double cy, double rx, double ry, double thickness, string? dash = null)
    {
        var e = new Ellipse
        {
            Width = rx * 2,
            Height = ry * 2,
            StrokeThickness = thickness
        };
        SetInk(e, Shape.StrokeProperty);
        Canvas.SetLeft(e, cx - rx);
        Canvas.SetTop(e, cy - ry);
        if (dash is not null) e.StrokeDashArray = ParseDashes(dash);
        return e;
    }

    private static Ellipse FilledDot(double cx, double cy, double r)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2 };
        SetInk(e, Shape.FillProperty);
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        return e;
    }

    private static TextBlock InkText(string text, double x, double y, double size)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = size
        };
        SetInk(t, TextBlock.ForegroundProperty);
        Canvas.SetLeft(t, x);
        Canvas.SetTop(t, y);
        return t;
    }

    private static void SetInk(FrameworkElement element, DependencyProperty property) =>
        element.SetResourceReference(property, InkBrushKey);

    private static DoubleCollection ParseDashes(string dash)
    {
        var dc = new DoubleCollection();
        foreach (var part in dash.Split(' ')) dc.Add(double.Parse(part, CultureInfo.InvariantCulture));
        return dc;
    }

    private static string Pt(double center, double r, double a) =>
        (center + r * Math.Cos(a)).ToString("0.00", CultureInfo.InvariantCulture) + " " +
        (center + r * Math.Sin(a)).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Inv(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
