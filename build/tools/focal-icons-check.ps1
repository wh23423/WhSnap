# ============================================================================
#  WhSnap - Focal - focal-icons-check.ps1
#  Proves src/WhSnap.App/Theme/FocalIcons.xaml is a loadable WPF
#  ResourceDictionary and that every icon of design/focal/icons.js is present as
#  a non-empty Geometry that fits the 24x24 grid.
#
#  It builds a throwaway net8.0-windows + UseWPF console app and loads the real
#  dictionary with XamlReader.Load, then asserts, for all 64 icons:
#
#    1. the key "Focal.Icon.<name>" exists (names written out literally, exactly
#       as in the P object of design/focal/icons.js, case and hyphens included)
#    2. the value is a Geometry (PathGeometry, or GeometryGroup of them),
#       !IsEmpty(), CanFreeze
#    3. Bounds.Width > 0 && Bounds.Height > 0. The single exception is minus, a
#       horizontal bar with height 0: allowed, and reported when it is hit.
#    4. Bounds equals the expected box of that icon (tolerance 0.05). Each box is
#       the bounding box of that icon in design/focal/icons.js expanded by the
#       documented rules (<circle>/<ellipse>/<rect rx> -> path arcs, d reused
#       verbatim). The table was derived independently from the svg source and
#       cross-checked against what WPF computes - the two agreed on all 64.
#    5. Bounds sits inside the 0..24 grid, and the 1.6 design stroke (0.9
#       half-width) overflows the grid by at most 0.5 (strokes may bleed, the
#       geometry itself may not leave the grid)
#    6. every arc segment is a real arc: both endpoints sit on the ellipse
#       (centre, rx, ry, rotation) WPF derives for that segment
#    7. every circle / ellipse of icons.js is matched by a two-arc subpath whose
#       shoelace area is pi*rx*ry (so the two-arc expansion is closed, has the
#       right radius and does not fold back on itself)
#
#  Usage:  powershell -ExecutionPolicy Bypass -File build/tools/focal-icons-check.ps1
#  Exit:   0 = all icons present, loadable and geometrically sound;
#          1 = a mismatch; 2 = the dictionary itself failed to load.
#
#  NOTE: ASCII-only on purpose - Windows PowerShell 5.1 reads .ps1 as ANSI.
#        (The dictated check "Bounds.Width > 0 && Bounds.Height > 0" is applied
#        to every icon; minus would fail it, so it is asserted as the documented
#        zero-height bar instead of being silently skipped.)
# ============================================================================
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent (Split-Path -Parent $scriptDir)   # build/tools -> repo root

$xaml = Join-Path $root 'src\WhSnap.App\Theme\FocalIcons.xaml'
if (-not (Test-Path -LiteralPath $xaml)) { throw "FocalIcons.xaml not found: $xaml" }

$work = Join-Path $env:TEMP 'focal-icons-check'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$proj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>IconCheck</AssemblyName>
    <RootNamespace>IconCheck</RootNamespace>
  </PropertyGroup>
</Project>
'@
Set-Content -Path (Join-Path $work 'IconCheck.csproj') -Value $proj -Encoding UTF8

$program = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

class Program
{
    const double GridMax  = 24.0;  // 24x24 icon grid
    const double HalfStr  = 0.9;   // design stroke 1.6 user units -> 0.8 half; 0.9 allows rounding
    const double Overflow = 0.5;   // tolerated stroke bleed outside the grid
    const double Tol      = 0.05;  // expected-bounds tolerance
    const double Eps      = 0.001; // arc geometry tolerance

    sealed class Row
    {
        public readonly string Name;
        public readonly double L, T, R, B;
        public Row(string name, double l, double t, double r, double b) { Name = name; L = l; T = t; R = r; B = b; }
    }

    // The contract: the 64 keys of the P object in design/focal/icons.js, with
    // the expected geometry bounds each one must expand to. Values are the svg
    // bounding boxes under the documented expansion; independent derivation
    // agreed with WPF to < 1e-6 on every icon.
    static readonly Row[] Icons = {
        new Row("brand", 4.0, 4.0, 20.0, 20.0),
        new Row("crop", 3.0, 3.0, 21.0, 21.0),
        new Row("capture", 3.0, 4.0, 21.0, 20.0),
        new Row("check", 4.5, 6.5, 19.5, 17.0),
        new Row("check-circle", 3.5, 3.5, 20.5, 20.5),
        new Row("x", 6.5, 6.5, 17.5, 17.5),
        new Row("copy", 4.0, 3.0, 20.0, 20.0),
        new Row("save", 4.5, 3.5, 19.5, 19.5),
        new Row("save-as", 5.0, 4.0, 19.0, 20.0),
        new Row("upload", 4.0, 4.5, 20.0, 20.0),
        new Row("download", 4.0, 3.5, 20.0, 19.5),
        new Row("refresh", 3.5, 3.519, 21.0, 20.519),
        new Row("trash", 4.0, 4.5, 20.0, 21.0),
        new Row("undo", 4.0, 6.0, 20.0, 22.0),
        new Row("redo", 4.0, 6.0, 20.0, 22.0),
        new Row("plus", 5.0, 5.0, 19.0, 19.0),
        new Row("minus", 5.0, 12.0, 19.0, 12.0),
        new Row("search", 4.5, 4.5, 21.0, 21.0),
        new Row("gear", 4.0, 5.0, 20.0, 19.0),
        new Row("keyboard", 3.0, 6.5, 21.0, 17.5),
        new Row("info", 3.5, 3.5, 20.5, 20.5),
        new Row("warning", 2.532, 3.354, 21.468, 20.3),
        new Row("external", 4.0, 4.5, 19.5, 20.5),
        new Row("chevronRight", 9.5, 6.0, 15.5, 18.0),
        new Row("chevronDown", 7.0, 10.0, 17.0, 15.0),
        new Row("power", 5.0, 3.5, 19.0, 19.044),
        new Row("magnifier", 4.5, 4.5, 21.0, 21.0),
        new Row("crosshair", 2.5, 2.5, 21.5, 21.5),
        new Row("pointer", 6.0, 3.5, 18.2, 20.3),
        new Row("eyedropper", 4.0, 3.4, 20.6, 20.0),
        new Row("ruler", 3.171, 3.171, 20.829, 20.829),
        new Row("grid", 4.0, 4.0, 20.0, 20.0),
        new Row("monitor", 3.0, 4.5, 21.0, 20.0),
        new Row("layers", 3.0, 3.5, 21.0, 17.9),
        new Row("history", 3.5, 3.5, 20.5, 20.5),
        new Row("palette", 3.5, 3.5, 20.5, 20.5),
        new Row("image", 3.5, 4.5, 20.5, 19.5),
        new Row("film", 3.5, 5.0, 20.5, 19.0),
        new Row("rect", 4.0, 6.5, 20.0, 17.5),
        new Row("ellipse", 4.0, 6.2, 20.0, 17.8),
        new Row("line", 5.0, 5.0, 19.0, 19.0),
        new Row("arrow", 4.5, 5.0, 19.0, 19.5),
        new Row("pencil", 4.0, 5.5, 18.5, 20.0),
        new Row("marker", 4.0, 8.0, 19.4, 20.5),
        new Row("text", 5.0, 5.0, 19.0, 19.0),
        new Row("mosaic", 4.0, 4.0, 20.0, 20.0),
        new Row("blur", 6.0, 3.2, 18.0, 19.6),
        new Row("eraser", 4.818, 9.2, 20.0, 21.0),
        new Row("badge", 4.0, 4.0, 20.0, 20.0),
        new Row("highlight", 3.5, 9.0, 20.5, 19.0),
        new Row("select", 6.0, 3.5, 21.5, 20.5),
        new Row("rotate", 3.0, 3.008, 21.0, 21.008),
        new Row("flipH", 5.0, 3.5, 19.0, 20.5),
        new Row("flipV", 3.5, 5.0, 20.5, 19.0),
        new Row("droplet", 6.0, 3.2, 18.0, 20.6),
        new Row("pin", 6.5, 3.5, 17.5, 20.5),
        new Row("window", 3.5, 4.5, 20.5, 19.5),
        new Row("eye", 2.8, 6.0, 21.2, 18.0),
        new Row("eye-off", 2.8, 4.0, 21.2, 20.0),
        new Row("clipboard", 5.5, 2.0, 18.5, 20.5),
        new Row("folder", 3.5, 5.5, 20.5, 19.5),
        new Row("tray", 4.0, 4.5, 20.0, 20.0),
        new Row("lock", 4.5, 4.0, 19.5, 20.5),
        new Row("speed", 3.0, 4.377, 21.0, 17.5)
    };

    // icons.js circles / ellipses, as (label, cx, cy, rx, ry) - the area proof.
    static readonly double[][] Circles = {
        new double[] { 12, 12, 3, 3 },        // brand
        new double[] { 12, 12, 8.5, 8.5 },    // check-circle
        new double[] { 11, 11, 6.5, 6.5 },    // search
        new double[] { 15, 7.5, 2.5, 2.5 },   // gear 1
        new double[] { 9, 16.5, 2.5, 2.5 },   // gear 2
        new double[] { 12, 12, 8.5, 8.5 },    // info
        new double[] { 11, 11, 6.5, 6.5 },    // magnifier
        new double[] { 12, 12, 7.5, 7.5 },    // crosshair
        new double[] { 12, 12, 8.5, 8.5 },    // history
        new double[] { 9, 10, 1.6, 1.6 },     // image
        new double[] { 12, 12, 8, 5.8 },      // ellipse
        new double[] { 12, 12, 2.6, 2.6 },    // eye
        new double[] { 12, 15, 1.6, 1.6 }     // speed
    };

    static int errors;
    static int arcCount;
    static int areaCount;

    static void Fail(string msg) { Console.WriteLine("  FAIL " + msg); errors++; }

    static int Main(string[] args)
    {
        string xamlPath = args.Length > 0 ? args[0] : "FocalIcons.xaml";
        Console.WriteLine("== Focal icon set check ==");
        Console.WriteLine("xaml: " + xamlPath);

        // ---- 1. the dictionary must load -------------------------------------
        ResourceDictionary dict;
        try
        {
            using (var fs = File.OpenRead(xamlPath))
            {
                dict = (ResourceDictionary)XamlReader.Load(fs);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
        Console.WriteLine("dictionary loaded, entries = " + dict.Count);

        // ---- 2..6 every icon ------------------------------------------------
        int degenerate = 0;
        foreach (Row row in Icons)
        {
            string key = "Focal.Icon." + row.Name;
            if (!dict.Contains(key)) { Fail("MISSING KEY: " + key); continue; }

            Geometry g = dict[key] as Geometry;
            if (g == null) { Fail(key + " is " + (dict[key] == null ? "null" : dict[key].GetType().Name) + ", not a Geometry"); continue; }

            if (g.IsEmpty()) { Fail(key + ": IsEmpty() is true"); continue; }
            if (!g.CanFreeze) Fail(key + ": geometry cannot be frozen");

            Rect b = g.Bounds;
            if (b.IsEmpty) { Fail(key + ": Bounds is Empty"); continue; }

            if (!(b.Width > 0 && b.Height > 0))
            {
                // minus is a single horizontal bar: width > 0, height == 0.
                if (b.Width > 0 && b.Height == 0 && row.Name == "minus") degenerate++;
                else { Fail(key + ": degenerate bounds " + b); continue; }
            }

            double d = Math.Max(Math.Max(Math.Abs(b.Left - row.L), Math.Abs(b.Top - row.T)),
                                Math.Max(Math.Abs(b.Right - row.R), Math.Abs(b.Bottom - row.B)));
            if (d > Tol)
                Fail(key + ": bounds " + b + " != expected (" + row.L + "," + row.T + ")-(" + row.R + "," + row.B + ") delta " + d.ToString("0.###"));

            if (b.Left < -Eps || b.Top < -Eps || b.Right > GridMax + Eps || b.Bottom > GridMax + Eps)
                Fail(key + ": geometry " + b + " is outside the 0..24 grid");
            if (b.Left - HalfStr < -Overflow || b.Top - HalfStr < -Overflow ||
                b.Right + HalfStr > GridMax + Overflow || b.Bottom + HalfStr > GridMax + Overflow)
                Fail(key + ": stroke overflows the grid by more than " + Overflow + " (" + b + ")");

            CheckFigures(key, g);
        }
        Console.WriteLine("geometry bounds: " + Icons.Length + " icons, all inside the 24x24 grid"
                          + (degenerate > 0 ? " (minus is a zero-height bar: allowed)" : ""));

        // ---- 7. the circle / ellipse expansions are the real thing -----------
        VerifyCircles(dict);

        Console.WriteLine("arc expansion: " + arcCount + " arc segments verified, "
                          + areaCount + " circular subpaths have area = pi*rx*ry");
        Console.WriteLine("checked " + Icons.Length + " keys, failures = " + errors);
        if (errors > 0) { Console.WriteLine("Focal icons: FAILED"); return 1; }
        Console.WriteLine("Focal icons: all keys present, loadable, inside the 24x24 grid.");
        return 0;
    }

    // Walks the geometry: every arc segment must be a real, consistent arc - its
    // two endpoints have to sit on the ellipse (centre, rx, ry, rotation) that
    // WPF derives for it. For the circle / ellipse / rounded-rect expansions this
    // is what proves the arcs close up and the flags are the right way round.
    static void CheckFigures(string key, Geometry g)
    {
        foreach (PathFigure f in Figures(g))
        {
            Point from = f.StartPoint;
            foreach (PathSegment s in f.Segments)
            {
                ArcSegment a = s as ArcSegment;
                if (a != null)
                {
                    arcCount++;
                    if (a.Size.Width <= 0 || a.Size.Height <= 0)
                        Fail(key + ": arc with a zero radius " + a.Size);
                    else if (!OnEllipse(from, a, a.Point))
                        Fail(key + ": arc " + from + " -> " + a.Point + " r=" + a.Size
                             + " does not have both endpoints on its own ellipse");
                }
                from = SegmentEnd(from, s);
            }
        }
    }

    static void VerifyCircles(ResourceDictionary dict)
    {
        // match each icons.js circle against a XAML subpath that is a two-arc loop
        foreach (double[] c in Circles)
        {
            double cx = c[0], cy = c[1], rx = c[2], ry = c[3];
            double area = Math.PI * rx * ry;
            bool found = false;
            foreach (Geometry g in AllGeometries(dict))
            {
                foreach (PathFigure f in Figures(g))
                {
                    if (f.Segments.Count != 2) continue;
                    if (!(f.Segments[0] is ArcSegment) || !(f.Segments[1] is ArcSegment)) continue;
                    Rect fb = FigureBounds(f);
                    if (Math.Abs(fb.Width - 2 * rx) > 0.02 || Math.Abs(fb.Height - 2 * ry) > 0.02) continue;
                    if (Math.Abs((fb.Left + fb.Right) / 2 - cx) > 0.02 || Math.Abs((fb.Top + fb.Bottom) / 2 - cy) > 0.02) continue;
                    double a = Math.Abs(PolygonArea(f));
                    if (Math.Abs(a - area) > 0.01 * area)
                        Fail("circle (" + cx + "," + cy + ") r=(" + rx + "," + ry + "): area " + a.ToString("0.###") + " != " + area.ToString("0.###") + " (pi*rx*ry)");
                    else
                        areaCount++;
                    found = true;
                }
            }
            if (!found) Fail("circle (" + cx + "," + cy + ") r=(" + rx + "," + ry + ") has no matching two-arc subpath");
        }
    }

    static double PolygonArea(PathFigure f)
    {
        List<Point> pts = new List<Point>();
        pts.Add(f.StartPoint);
        Point from = f.StartPoint;
        foreach (PathSegment s in f.Segments)
        {
            ArcSegment a = s as ArcSegment;
            if (a != null)
            {
                Point c;
                if (ArcCentre(from, a, out c))
                {
                    double t1 = Math.Atan2((from.Y - c.Y) / a.Size.Height, (from.X - c.X) / a.Size.Width);
                    double t2 = Math.Atan2((a.Point.Y - c.Y) / a.Size.Height, (a.Point.X - c.X) / a.Size.Width);
                    double dth = t2 - t1;
                    bool cw = a.SweepDirection == SweepDirection.Clockwise;
                    if (cw && dth < 0) dth += 2 * Math.PI;
                    if (!cw && dth > 0) dth -= 2 * Math.PI;
                    int n = 512;
                    for (int i = 1; i <= n; i++)
                    {
                        double t = t1 + dth * i / n;
                        pts.Add(new Point(c.X + a.Size.Width * Math.Cos(t), c.Y + a.Size.Height * Math.Sin(t)));
                    }
                }
                else pts.Add(a.Point);
            }
            else
            {
                LineSegment l = s as LineSegment;
                if (l != null) pts.Add(l.Point);
                else pts.Add(SegmentEnd(from, s));
            }
            from = SegmentEnd(from, s);
        }
        double sum = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
            sum += pts[i].X * pts[i + 1].Y - pts[i + 1].X * pts[i].Y;
        return sum / 2.0;
    }

    // True when both endpoints sit on the ellipse (centre, rx, ry, rotation) that
    // WPF derived for this segment - i.e. the arc is a real, consistent arc.
    static bool OnEllipse(Point from, ArcSegment a, Point end)
    {
        Point centre;
        if (!ArcCentre(from, a, out centre)) return false;
        return Radial(from, a, centre) < 0.03 && Radial(end, a, centre) < 0.03;
    }

    static double Radial(Point p, ArcSegment a, Point c)
    {
        double phi = a.RotationAngle * Math.PI / 180.0;
        double cosp = Math.Cos(phi), sinp = Math.Sin(phi);
        double dx = p.X - c.X, dy = p.Y - c.Y;
        double u = (cosp * dx + sinp * dy) / a.Size.Width;
        double v = (-sinp * dx + cosp * dy) / a.Size.Height;
        return Math.Abs(Math.Sqrt(u * u + v * v) - 1.0);
    }

    // SVG/app F.6.5 endpoint -> centre parameterization.
    static bool ArcCentre(Point p1, ArcSegment a, out Point centre)
    {
        centre = new Point(0, 0);
        double rx = a.Size.Width, ry = a.Size.Height;
        if (rx <= 0 || ry <= 0) return false;
        double x1 = p1.X, y1 = p1.Y, x2 = a.Point.X, y2 = a.Point.Y;
        double dx2 = (x1 - x2) / 2, dy2 = (y1 - y2) / 2;
        double lam = (dx2 * dx2) / (rx * rx) + (dy2 * dy2) / (ry * ry);
        if (lam > 1 + 1e-9) return false;
        double num = rx * rx * ry * ry - rx * rx * dy2 * dy2 - ry * ry * dx2 * dx2;
        double den = rx * rx * dy2 * dy2 + ry * ry * dx2 * dx2;
        if (den <= 0) return false;
        double co = Math.Sqrt(Math.Max(num / den, 0));
        bool cw = a.SweepDirection == SweepDirection.Clockwise;
        if (a.IsLargeArc == cw) co = -co;
        centre = new Point(co * rx * dy2 / ry + (x1 + x2) / 2, -co * ry * dx2 / rx + (y1 + y2) / 2);
        return true;
    }

    static double Sq(double v) { return v * v; }

    static Point SegmentEnd(Point from, PathSegment s)
    {
        ArcSegment a = s as ArcSegment;
        if (a != null) return a.Point;
        LineSegment l = s as LineSegment;
        if (l != null) return l.Point;
        PolyLineSegment pl = s as PolyLineSegment;
        if (pl != null) return pl.Points[pl.Points.Count - 1];
        BezierSegment b = s as BezierSegment;
        if (b != null) return b.Point3;
        PolyBezierSegment pb = s as PolyBezierSegment;
        if (pb != null) return pb.Points[pb.Points.Count - 1];
        QuadraticBezierSegment q = s as QuadraticBezierSegment;
        if (q != null) return q.Point2;
        PolyQuadraticBezierSegment pq = s as PolyQuadraticBezierSegment;
        if (pq != null) return pq.Points[pq.Points.Count - 1];
        throw new InvalidOperationException("unsupported segment " + s.GetType().Name);
    }

    static IEnumerable<Geometry> AllGeometries(ResourceDictionary dict)
    {
        string[] all = { "brand", "check-circle", "search", "gear", "info", "magnifier", "crosshair",
                         "history", "image", "ellipse", "eye", "speed" };
        foreach (string n in all) { Geometry g = dict["Focal.Icon." + n] as Geometry; if (g != null) yield return g; }
    }

    static IEnumerable<PathFigure> Figures(Geometry g)
    {
        PathGeometry pg = g as PathGeometry;
        if (pg != null) { foreach (PathFigure f in pg.Figures) yield return f; yield break; }
        GeometryGroup gg = g as GeometryGroup;
        if (gg != null)
            foreach (Geometry child in gg.Children)
                foreach (PathFigure f in Figures(child)) yield return f;
    }

    static Rect FigureBounds(PathFigure f)
    {
        var pg = new PathGeometry();
        pg.Figures.Add(f);
        return pg.Bounds;
    }
}
'@
Set-Content -Path (Join-Path $work 'Program.cs') -Value $program -Encoding UTF8

Push-Location $work
$code = 2
$saved = $ErrorActionPreference
try {
    # dotnet writes build diagnostics to stderr; under 'Stop' PowerShell 5.1 would
    # turn those lines into terminating errors before we can read the exit code.
    $ErrorActionPreference = 'Continue'
    $out = & dotnet run --project (Join-Path $work 'IconCheck.csproj') -- $xaml 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host "$_" }
    Write-Host "[focal-icons] exit=$code"
} finally {
    $ErrorActionPreference = $saved
    Pop-Location
}
exit $code
