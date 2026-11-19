# Draws the IGYO icon (his white ten-point star on a slate tile running from #595B6B at the top to #222328 at the bottom)
# as a multi-size .ico and a PNG logo.
# -SvgOut also writes the mark by itself as an SVG (white unless -SvgColor); -GlyphOut writes its outline for a
# 24 x 24 icon grid, as the path data Assets\Shared\icons.js holds for the favourites icon of the Specimen kit.
# The defaults draw the icon IGYO ships with: his own icon, the same in every frame.
#   -Shape            spark (the mark now), thorns, ball or star (the marks before it, see below)
# The spark (17 Nov 2026 night, his "Go with this one" with the icon as SVG): his star exactly as his path draws it, on
# his 256 x 256 box, the tile's corner 44.48 of 256 (88.96 on the 512 canvas), the slate running top to bottom.
# The thorned swirl before it (T2) was acid on the dark tile: -Shape thorns.
#   -ThornFill        how much of the tile the thorned swirl's square fills: frames up to 20 px, 24-32, 40-48, larger
# The ball (17 Nov 2026 afternoon) was dark on the acid tile: -Shape ball -Tile '#C5F135' -Ink '#16171B' -BallFill 0.92,0.88,0.8,0.74
#   -Tile, -Ink       the tile's and the mark's colors (#RRGGBB); -Tile none: the mark alone, on nothing
#   -Edge, -EdgePx    a line around the mark in that color, about this many pixels wide in every frame (it keeps the
#                     acid mark readable on a light background, where acid alone is about 1.3:1 against white)
#   -Tile2            with a second color the tile runs from -Tile (top left) to -Tile2 (bottom right)
#                     (his spark's tile runs straight down, top to bottom)
#   -TileMid          with -Tile2: a third color the tile passes through halfway
#   -Rim, -RimWidth   a line around the tile's edge, in that color, this wide on the 512 px tile
# The app icons to choose from in Settings (Assets\AppIcons) are drawn by tools\make-app-icons.ps1 with these switches.
# The ball:
#   -Spikes           how many times as long the spikes are drawn as listed below (1 = as listed)
#   -BallFill         how much of the tile the mark fills: in frames up to 20 px, of 24-32 px, of 40-48 px, and larger
# src\Setup\Setup.cs draws the same mark in the setup window (its Mark class): change both together. After a change,
# write Assets\Shared\mark.svg again (-SvgOut, -SvgColor '#000000') and the path in icons.js (-GlyphOut).
#
# The star (-Shape star) was the mark until 17 Nov 2026: needles from a centre, after the "spiky" UAP photos. With no
# other switch it draws that icon exactly, dark on the acid tile, with a chunkier mark in the small frames (at 24 px
# the large mark's needles came out thinner than a pixel and the star read as a faint speck on a dark taskbar):
#   -SmallScale       how much of the tile the mark fills in frames of 20 px and below (-MidScale: 24 and 32 px;
#                     0 there: halfway to 0.72 at 24 px and 0.72 at 32 px)
#   -SmallBold        how much wider the needles are in frames of 32 px and below (halfway there at 40-48 px)
#   -SmallPinch       how far the needles' sides curve in, in those frames (0.36 as in the large mark; 1 = straight)
#   -StubsFrom        the four short needles are drawn only in frames of at least this many pixels
# Before that, until earlier on 17 Nov 2026, it was a white star on #24262C with the large mark in every frame:
#   -Shape star -Tile '#24262C' -Ink '#FFFFFF' -SmallScale 0.8 -MidScale 0 -SmallBold 1 -SmallPinch 0.36 -StubsFrom 0
param([Parameter(Mandatory)][string]$OutFile, [string]$PngOut, [int]$PngSize = 256, [string]$SvgOut, [string]$SvgColor = '#FFFFFF', [double]$SmallScale = 0.94,
      [double]$MidScale = 0.9, [string]$Tile = '', [string]$Tile2 = '', [string]$TileMid = '', [string]$Ink = '', [string]$Edge = '', [double]$EdgePx = 0.9,
      [double]$SmallBold = 2.6,
      [double]$SmallPinch = 0.7, [int]$StubsFrom = 64, [string]$Rim = '', [double]$RimWidth = 16,
      [ValidateSet('spark', 'thorns', 'ball', 'star')][string]$Shape = 'spark', [double]$Spikes = 1, [double[]]$BallFill = @(0.92, 0.88, 0.8, 0.74),
      [double[]]$ThornFill = @(0.86, 0.82, 0.8, 0.78),
      [string]$GlyphOut)
Add-Type -AssemblyName System.Drawing
# The colors when none are given: his star white on the slate running down to near-black; the thorned swirl in acid on
# the dark tile; the earlier marks dark on the acid tile.
if ($Shape -eq 'spark' -and -not $Tile -and -not $Tile2) { $Tile = '#595B6B'; $Tile2 = '#222328' }
if (-not $Tile) { $Tile = if ($Shape -eq 'thorns') { '#16171B' } else { '#C5F135' } }
if (-not $Ink) { $Ink = if ($Shape -eq 'spark') { '#FFFFFF' } elseif ($Shape -eq 'thorns') { '#C5F135' } else { '#16171B' } }

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

// The mark on a 512 x 512 canvas around (256, 256): a ball with ten spikes, mirror-symmetric about the vertical, one
// long spike straight up and the same one straight down. Plain geometry: straight sides between tips and notches,
// every tip rounded with one radius and every notch with another. (How it came about: the outline of a ball with
// spikes, made symmetrical, the top spike repeated at the bottom, the spikes drawn out 1.6 times as far, and then
// all of it redrawn with straight sides at the same tips and notches.)
public static class IGYOBall
{
    // The right half's corners from the top tip round to the bottom tip, a tip and a notch by turns: the angle from
    // straight up (degrees, clockwise) and the distance from the centre. The left half is the mirror image.
    static readonly double[][] Corners = {
        new double[] { 0, 251 }, new double[] { 19, 120 },       // the top spike, and the notch after it
        new double[] { 42, 225 }, new double[] { 54, 128 },      // the upper diagonal spike
        new double[] { 66, 254 }, new double[] { 87, 122 },      // the upper side spike
        new double[] { 109, 241 }, new double[] { 118, 120 },    // the lower side spike
        new double[] { 137, 211 }, new double[] { 161, 120 },    // the lower diagonal spike
        new double[] { 180, 251 },                               // the bottom spike
    };
    const double TipRound = 6, NotchRound = 8;   // the radius a tip, and a notch, is rounded with
    const double Box = 470;                      // the mark's longer side on the canvas

    // The whole outline as points, `per` steps to a rounded corner. `spikes` draws every spike that many times as
    // long, measured from the deepest notch (1 = as listed); the outline is then fitted to the box, centred.
    public static List<double[]> Outline(double spikes = 1, int per = 12)
    {
        double low = double.MaxValue;
        for (int i = 1; i < Corners.Length; i += 2) low = Math.Min(low, Corners[i][1]);
        var c = new List<double[]>();
        for (int i = 0; i < Corners.Length; i++)
        {
            double a = Corners[i][0] * Math.PI / 180, d = i % 2 == 0 ? low + spikes * (Corners[i][1] - low) : Corners[i][1];
            c.Add(new[] { 256 + d * Math.Sin(a), 256 - d * Math.Cos(a) });
        }
        for (int i = Corners.Length - 2; i >= 1; i--) c.Add(new[] { 512 - c[i][0], c[i][1] });

        // each corner becomes an arc that touches both of its sides
        var all = new List<double[]>();
        int n = c.Count;
        for (int i = 0; i < n; i++)
        {
            double[] p = c[i], q = c[(i + n - 1) % n], s = c[(i + 1) % n];
            double ax = q[0] - p[0], ay = q[1] - p[1], bx = s[0] - p[0], by = s[1] - p[1];
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            ax /= la; ay /= la; bx /= lb; by /= lb;
            double half = Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + ay * by))) / 2;
            double t = Math.Min((i % 2 == 0 ? TipRound : NotchRound) / Math.Tan(half), 0.45 * Math.Min(la, lb)), r = t * Math.Tan(half);
            double mx = ax + bx, my = ay + by, lm = Math.Sqrt(mx * mx + my * my);
            double ox = p[0] + mx / lm * r / Math.Sin(half), oy = p[1] + my / lm * r / Math.Sin(half);
            double from = Math.Atan2(p[1] + ay * t - oy, p[0] + ax * t - ox), sweep = Math.Atan2(p[1] + by * t - oy, p[0] + bx * t - ox) - from;
            while (sweep > Math.PI) sweep -= 2 * Math.PI;
            while (sweep < -Math.PI) sweep += 2 * Math.PI;
            for (int k = 0; k <= per; k++) all.Add(new[] { ox + r * Math.Cos(from + sweep * k / per), oy + r * Math.Sin(from + sweep * k / per) });
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in all)
        {
            minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]); minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
        }
        double fit = Box / Math.Max(maxX - minX, maxY - minY), cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        foreach (var p in all) { p[0] = 256 + (p[0] - cx) * fit; p[1] = 256 + (p[1] - cy) * fit; }
        return all;
    }

    public static GraphicsPath ToPath(double scale, double ox, double oy, double spikes)
    {
        var outline = Outline(spikes);
        var pts = new PointF[outline.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF((float)(outline[i][0] * scale + ox), (float)(outline[i][1] * scale + oy));
        var path = new GraphicsPath(FillMode.Winding);
        path.AddPolygon(pts);
        return path;
    }

    // The outline as path data on a grid of `grid` units, the mark's longer side `side` of them, centred.
    public static string SvgPathData(double spikes, double grid = 512, double side = Box, int per = 8, string digits = "0.##")
    {
        var sb = new StringBuilder();
        var outline = Outline(spikes, per);
        double k = side / Box, c = grid / 2;
        for (int i = 0; i < outline.Count; i++)
            sb.Append(i == 0 ? "M" : "L").Append((c + (outline[i][0] - 256) * k).ToString(digits, CultureInfo.InvariantCulture))
              .Append(',').Append((c + (outline[i][1] - 256) * k).ToString(digits, CultureInfo.InvariantCulture));
        return sb.Append('Z').ToString();
    }
}

// The mark until 17 Nov 2026 (-Shape star), on the same canvas: needles from the centre, mirror-symmetric about the
// vertical. Each needle starts a little behind the centre, its sides curve slightly inward, and it ends in a sharp tip.
public static class IGYOMark
{
    // angle (degrees; 0 = right, -90 = up), length, half-width at the base
    static readonly double[][] Needles = {
        new double[] { -90, 241, 21 }, new double[] { -25, 246, 21 }, new double[] { -155, 246, 21 },   // up, and either side
        new double[] { 40, 200, 21 }, new double[] { 140, 200, 21 },                                   // the lower pair
        new double[] { 90, 150, 17 },                                                                  // the short one at the bottom
        new double[] { -51, 98, 15 }, new double[] { -129, 98, 15 }, new double[] { 9, 88, 14 }, new double[] { 171, 88, 14 },
    };
    const double Back = 14, Waist = 0.42, Pinch = 0.36;

    static List<double[]> QBez(double[] p, double[] c, double[] q, int n)
    {
        var o = new List<double[]>();
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            o.Add(new[] { u * u * p[0] + 2 * u * t * c[0] + t * t * q[0], u * u * p[1] + 2 * u * t * c[1] + t * t * q[1] });
        }
        return o;
    }

    // One outline per needle, all wound the same way, so non-zero filling joins them where they overlap.
    // `wide` widens every needle (small frames); without `stubs` the last four, the short ones, are left out;
    // `pinch` is how far the sides curve in (1: straight sides, a plain spike).
    public static List<List<double[]>> Polygons(double wide = 1, bool stubs = true, double pinch = Pinch)
    {
        var all = new List<List<double[]>>();
        for (int i = 0; i < Needles.Length; i++)
        {
            if (!stubs && i >= Needles.Length - 4) continue;
            var nd = Needles[i];
            double a = nd[0] * Math.PI / 180, len = nd[1], w = nd[2] * wide;
            double dx = Math.Cos(a), dy = Math.Sin(a), nx = -dy, ny = dx;
            double bx = 256 - dx * Back, by = 256 - dy * Back;
            double mx = 256 + dx * len * Waist, my = 256 + dy * len * Waist;
            double[] b0 = { bx + nx * w, by + ny * w }, b1 = { bx - nx * w, by - ny * w }, tip = { 256 + dx * len, 256 + dy * len };
            double[] c0 = { mx + nx * w * pinch, my + ny * w * pinch }, c1 = { mx - nx * w * pinch, my - ny * w * pinch };
            var poly = QBez(b0, c0, tip, 40);
            var back = QBez(tip, c1, b1, 40);
            poly.AddRange(back.GetRange(1, back.Count - 1));
            all.Add(poly);
        }
        return all;
    }

    public static GraphicsPath ToPath(double scale, double ox, double oy, double wide = 1, bool stubs = true, double pinch = Pinch)
    {
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var poly in Polygons(wide, stubs, pinch))
        {
            var pts = new PointF[poly.Count];
            for (int i = 0; i < poly.Count; i++) pts[i] = new PointF((float)(poly[i][0] * scale + ox), (float)(poly[i][1] * scale + oy));
            path.AddPolygon(pts);
        }
        return path;
    }

    public static string SvgPathData()
    {
        var sb = new StringBuilder();
        foreach (var poly in Polygons())
        {
            for (int i = 0; i < poly.Count; i++)
                sb.Append(i == 0 ? "M" : "L").Append(poly[i][0].ToString("0.##", CultureInfo.InvariantCulture))
                  .Append(',').Append(poly[i][1].ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append('Z');
        }
        return sb.ToString();
    }
}

// The mark since 17 Nov 2026 evening (his "I like T2", -Shape thorns): five blades from a star at the heart, each
// bending as it goes out into a hooked point, with two barbs along its outer edge pointing back. Drawn on the -1..1
// square (y down) and laid on the 512 x 512 canvas around (256, 256). Every figure is wound the same way, so where
// they overlap they join (filled with the winding rule).
public static class IGYOThorns
{
    const int Blades = 5, Steps = 160;
    const double Inner = 0.16, Tip = 0.98, Bend = 1.55, Width = 0.22, Hook = 2.0, Lean = 0.25;
    // each barb: where along the blade (0..1), how long, and how far back its point leans
    static readonly double[][] Barbs = { new[] { 0.38, 0.14, 0.10 }, new[] { 0.66, 0.11, 0.09 } };

    public static List<double[][]> Figures()
    {
        var figures = new List<double[][]>();
        for (int i = 0; i < Blades; i++) figures.Add(Blade(i * 2 * Math.PI / Blades - Math.PI / 2));
        figures.Add(Circle(0.21, 40));                                    // under the star, so its points meet the blades
        figures.Add(Star(5, 0.30, 0.15, -Math.PI / 2 + 0.3));
        for (int i = 0; i < figures.Count; i++)
        {
            double area = 0;
            var f = figures[i];
            for (int j = 0; j < f.Length; j++) { var a = f[j]; var b = f[(j + 1) % f.Length]; area += a[0] * b[1] - b[0] * a[1]; }
            if (area < 0) Array.Reverse(f);
        }
        return figures;
    }

    static double[] Lerp(double[][] arr, double t)
    {
        double i = t * (arr.Length - 1);
        int lo = (int)Math.Floor(i), hi = Math.Min(lo + 1, arr.Length - 1);
        double f = i - lo;
        return new[] { arr[lo][0] * (1 - f) + arr[hi][0] * f, arr[lo][1] * (1 - f) + arr[hi][1] * f };
    }

    static double[][] Blade(double a0)
    {
        int n = Steps;
        var spine = new double[n][]; var normal = new double[n][]; var outer = new double[n][]; var inner = new double[n][];
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1), ang = a0 + Bend * Math.Pow(t, Hook), r = Inner + (Tip - Inner) * t;
            spine[i] = new[] { Math.Cos(ang) * r, Math.Sin(ang) * r };
            w[i] = Width * Math.Pow(1 - t, 1.15);
        }
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            double tx = spine[b][0] - spine[a][0], ty = spine[b][1] - spine[a][1], l = Math.Sqrt(tx * tx + ty * ty);
            normal[i] = new[] { ty / l, -tx / l };                       // towards the outer (convex) side
            outer[i] = new[] { spine[i][0] + normal[i][0] * w[i] * (1 + Lean), spine[i][1] + normal[i][1] * w[i] * (1 + Lean) };
            inner[i] = new[] { spine[i][0] - normal[i][0] * w[i] * (1 - Lean), spine[i][1] - normal[i][1] * w[i] * (1 - Lean) };
        }
        var pts = new List<double[]>();
        int bi = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            while (bi < Barbs.Length && t >= Barbs[bi][0])
            {
                double tb = Barbs[bi][0], length = Barbs[bi][1], back = Barbs[bi][2];
                var p1 = Lerp(outer, Math.Max(0, tb - 0.05));
                var p2 = Lerp(outer, Math.Min(1, tb + 0.035));
                var o = Lerp(outer, Math.Max(0, tb - back));
                var nn = Lerp(normal, Math.Max(0, tb - back));
                pts.Add(p1);
                pts.Add(new[] { o[0] + nn[0] * length, o[1] + nn[1] * length });
                pts.Add(p2);
                bi++;
            }
            pts.Add(outer[i]);
        }
        for (int i = n - 1; i >= 0; i--) pts.Add(inner[i]);
        return pts.ToArray();
    }

    static double[][] Circle(double r, int steps)
    {
        var pts = new double[steps][];
        for (int i = 0; i < steps; i++) pts[i] = new[] { Math.Cos(i * 2 * Math.PI / steps) * r, Math.Sin(i * 2 * Math.PI / steps) * r };
        return pts;
    }

    static double[][] Star(int points, double outer, double inner, double phase)
    {
        var pts = new double[points * 2][];
        for (int i = 0; i < points * 2; i++)
        {
            double r = i % 2 == 0 ? outer : inner, a = phase + i * Math.PI / points;
            pts[i] = new[] { Math.Cos(a) * r, Math.Sin(a) * r };
        }
        return pts;
    }

    // How sharply the outline turns at b (radians).
    static double Turn(double[] a, double[] b, double[] c)
    {
        double a1 = Math.Atan2(b[1] - a[1], b[0] - a[0]), a2 = Math.Atan2(c[1] - b[1], c[0] - b[0]), d = Math.Abs(a2 - a1);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    // On the 512 canvas, scaled and moved like the other marks' ToPath.
    public static GraphicsPath ToPath(double scale, double ox, double oy)
    {
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var f in Figures())
        {
            var pts = new PointF[f.Length];
            for (int i = 0; i < f.Length; i++) pts[i] = new PointF((float)((256 + f[i][0] * 256) * scale + ox), (float)((256 + f[i][1] * 256) * scale + oy));
            path.AddPolygon(pts);
        }
        return path;
    }

    // As path data on a grid of `grid` units, the mark's longer side `side` of them, centred (one subpath a figure).
    // Points closer than `least` units to the last one written are left out (the outline is drawn finely for the icon).
    public static string SvgPathData(double grid = 512, double side = 0, string digits = "0.##", double least = 0)
    {
        if (least <= 0) least = grid / 80;
        var figures = Figures();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var f in figures) foreach (var p in f) { minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]); minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]); }
        double k = side > 0 ? side / Math.Max(maxX - minX, maxY - minY) : grid / 2, cx = side > 0 ? (minX + maxX) / 2 : 0, cy = side > 0 ? (minY + maxY) / 2 : 0;
        var sb = new StringBuilder();
        foreach (var f in figures)
        {
            double lastX = double.NaN, lastY = double.NaN;
            for (int i = 0; i < f.Length; i++)
            {
                double x = grid / 2 + (f[i][0] - cx) * k, y = grid / 2 + (f[i][1] - cy) * k;
                bool corner = i > 0 && i < f.Length - 1 && Turn(f[i - 1], f[i], f[i + 1]) > 0.5;   // a barb's or star's point: always kept
                if (i > 0 && !corner && Math.Abs(x - lastX) + Math.Abs(y - lastY) < least) continue;
                sb.Append(i == 0 ? "M" : "L").Append(x.ToString(digits, CultureInfo.InvariantCulture)).Append(',').Append(y.ToString(digits, CultureInfo.InvariantCulture));
                lastX = x; lastY = y;
            }
            sb.Append('Z');
        }
        return sb.ToString();
    }
}

// The mark since 17 Nov 2026 night, his own drawing ("Go with this one"): a ten-point star, the long spikes straight up
// and down, a long pair either side a little off the level, a short pair on each diagonal, its tips softly round. His
// SVG's path exactly, as cubic curves on his 256 x 256 icon box, where the star stands centred (199 x 203 of it): a
// start, then three points a curve (his two short straight pieces as curves too). src\Setup\Setup.cs has the same.
public static class IGYOSpark
{
    static readonly float[] P =
    {
        226.25f, 173.44f, 220.21f, 165.6f, 210.02f, 159.52f, 202.34f, 153.46f, 197.24f, 149.44f, 192.03f, 145.55f,
        186.97f, 141.47f, 184.44f, 139.43f, 181.94f, 137.35f, 179.51f, 135.19f, 176.03f, 132.1f, 171.99f, 129.41f,
        175.16f, 124.4f, 177.25f, 121.1f, 181.3f, 119.3f, 184.17f, 116.96f, 193.31f, 109.51f, 202.68f, 102.36f,
        211.87f, 94.98f, 215.66f, 91.94f, 219.47f, 88.9f, 223.07f, 85.64f, 223.91f, 84.88f, 227.7f, 82.16f,
        227.39f, 80.72f, 226.44f, 79.31f, 225.54f, 79.2f, 223.95f, 79.16f, 221.19f, 79.09f, 218.27f, 80.35f,
        215.74f, 81.3f, 207.18f, 84.53f, 198.96f, 88.65f, 190.63f, 92.43f, 186.74f, 94.2f, 182.84f, 95.95f,
        178.84f, 97.46f, 176.79f, 98.23f, 174.74f, 99.05f, 172.57f, 99.43f, 171.42f, 99.63f, 169.16f, 100.06f,
        168.17f, 99.12f, 166.67f, 97.69f, 168.69f, 94.66f, 169.31f, 93.23f, 170.21f, 91.14f, 171.23f, 89.1f,
        172.27f, 87.07f, 173.78f, 84.12f, 175.35f, 81.21f, 176.89f, 78.28f, 177.89f, 76.39f, 179.05f, 74.47f,
        179.73f, 72.44f, 180.05f, 70.98f, 178.61f, 70.17f, 177.37f, 70.06f, 175.75f, 69.92f, 173.94f, 70.84f,
        172.51f, 71.47f, 169.99f, 72.57f, 167.56f, 73.86f, 165.14f, 75.16f, 160.89f, 77.44f, 156.7f, 79.83f,
        152.42f, 82.04f, 150.63f, 82.96f, 148.82f, 83.87f, 146.96f, 84.64f, 145.8f, 85.12f, 143.27f, 86.38f,
        142.09f, 85.69f, 141.68f, 85.45f, 141.37f, 85.06f, 141.08f, 84.68f, 139.55f, 82.65f, 139.41f, 79.62f,
        138.94f, 77.16f, 138.28f, 73.72f, 137.61f, 70.29f, 136.95f, 66.85f, 135.34f, 58.52f, 133.73f, 50.19f,
        132.13f, 41.86f, 131.29f, 37.53f, 130.46f, 33.2f, 129.62f, 28.87f, 129.27f, 27.86f, 128.72f, 27.14f,
        128f, 26.67f, 127.29f, 27.14f, 126.73f, 27.86f, 126.38f, 28.87f, 125.54f, 33.2f, 124.71f, 37.53f,
        123.87f, 41.86f, 122.26f, 50.19f, 120.65f, 58.52f, 119.05f, 66.85f, 118.39f, 70.29f, 117.72f, 73.72f,
        117.06f, 77.16f, 116.59f, 79.62f, 116.45f, 82.65f, 114.92f, 84.68f, 114.63f, 85.06f, 114.33f, 85.45f,
        113.91f, 85.69f, 112.73f, 86.38f, 110.2f, 85.12f, 109.04f, 84.64f, 107.18f, 83.87f, 105.37f, 82.96f,
        103.58f, 82.04f, 99.29f, 79.83f, 95.11f, 77.44f, 90.86f, 75.16f, 88.44f, 73.86f, 86.01f, 72.57f,
        83.49f, 71.47f, 82.06f, 70.84f, 80.25f, 69.92f, 78.63f, 70.06f, 77.38f, 70.17f, 75.95f, 70.98f,
        76.27f, 72.44f, 76.95f, 74.47f, 78.12f, 76.39f, 79.11f, 78.28f, 80.65f, 81.21f, 82.23f, 84.12f,
        83.73f, 87.07f, 84.77f, 89.1f, 85.78f, 91.14f, 86.69f, 93.23f, 87.31f, 94.67f, 89.33f, 97.69f,
        87.83f, 99.12f, 86.85f, 100.06f, 84.59f, 99.63f, 83.43f, 99.43f, 81.27f, 99.06f, 79.21f, 98.24f,
        77.16f, 97.46f, 73.16f, 95.95f, 69.26f, 94.2f, 65.37f, 92.43f, 57.04f, 88.65f, 48.82f, 84.53f,
        40.26f, 81.3f, 37.73f, 80.35f, 34.81f, 79.08f, 32.05f, 79.16f, 30.46f, 79.2f, 29.56f, 79.31f,
        28.61f, 80.72f, 28.3f, 82.16f, 32.09f, 84.88f, 32.93f, 85.64f, 36.53f, 88.9f, 40.34f, 91.94f,
        44.13f, 94.98f, 53.32f, 102.36f, 62.7f, 109.51f, 71.83f, 116.96f, 74.71f, 119.31f, 78.76f, 121.1f,
        80.84f, 124.4f, 84.01f, 129.41f, 79.97f, 132.1f, 76.49f, 135.19f, 74.06f, 137.35f, 71.56f, 139.43f,
        69.03f, 141.47f, 63.97f, 145.54f, 58.76f, 149.44f, 53.66f, 153.46f, 45.97f, 159.52f, 35.78f, 165.6f,
        29.75f, 173.44f, 29.56f, 173.69f, 28.32f, 174.85f, 28.61f, 175.28f, 29.56f, 176.69f, 30.46f, 176.8f,
        32.05f, 176.84f, 35.91f, 176.94f, 40.13f, 174.8f, 43.6f, 173.37f, 49.35f, 171f, 54.98f, 168.34f,
        60.62f, 165.74f, 65.57f, 163.46f, 70.53f, 161.15f, 75.6f, 159.14f, 78.15f, 158.13f, 84.64f, 154.89f,
        87.25f, 157.1f, 87.97f, 157.71f, 88.13f, 158.77f, 87.98f, 159.69f, 87.24f, 164.17f, 84.17f, 168.14f,
        82.1f, 172.06f, 80.86f, 174.41f, 79.6f, 176.75f, 78.37f, 179.1f, 77.66f, 180.46f, 76.59f, 182.03f,
        76.26f, 183.55f, 75.94f, 185.01f, 77.38f, 185.82f, 78.62f, 185.93f, 79.69f, 186.02f, 80.82f, 185.58f,
        81.8f, 185.21f, 83.44f, 184.6f, 85.02f, 183.83f, 86.59f, 183.05f, 90.63f, 181.03f, 94.56f, 178.8f,
        98.53f, 176.64f, 102.11f, 174.69f, 105.72f, 172.66f, 109.52f, 171.15f, 110.5f, 170.76f, 111.47f, 170.3f,
        112.51f, 170.12f, 113.97f, 169.88f, 114.67f, 170.8f, 115.41f, 171.97f, 116.27f, 173.32f, 116.35f, 175.15f,
        116.65f, 176.69f, 117.23f, 179.71f, 117.81f, 182.72f, 118.4f, 185.74f, 119.16f, 189.67f, 119.92f, 193.59f,
        120.67f, 197.52f, 121.46f, 201.59f, 122.24f, 205.66f, 123.03f, 209.73f, 123.7f, 213.18f, 124.36f, 216.63f,
        125.03f, 220.08f, 125.43f, 222.15f, 125.83f, 224.21f, 126.23f, 226.28f, 126.28f, 226.56f, 126.34f, 226.83f,
        126.39f, 227.11f, 126.74f, 228.12f, 127.29f, 228.84f, 128.01f, 229.31f, 128.72f, 228.84f, 129.28f, 228.12f,
        129.63f, 227.11f, 129.68f, 226.83f, 129.74f, 226.56f, 129.79f, 226.28f, 130.19f, 224.21f, 130.59f, 222.15f,
        130.99f, 220.08f, 131.66f, 216.63f, 132.32f, 213.18f, 132.99f, 209.73f, 133.78f, 205.66f, 134.56f, 201.59f,
        135.35f, 197.52f, 136.11f, 193.59f, 136.87f, 189.67f, 137.62f, 185.74f, 138.2f, 182.72f, 138.78f, 179.71f,
        139.37f, 176.69f, 139.67f, 175.15f, 139.75f, 173.32f, 140.61f, 171.97f, 141.35f, 170.8f, 142.05f, 169.88f,
        143.51f, 170.12f, 144.56f, 170.29f, 145.53f, 170.76f, 146.5f, 171.15f, 150.3f, 172.66f, 153.9f, 174.68f,
        157.49f, 176.64f, 161.46f, 178.8f, 165.39f, 181.03f, 169.43f, 183.05f, 170.99f, 183.83f, 172.58f, 184.6f,
        174.22f, 185.21f, 175.2f, 185.58f, 176.33f, 186.02f, 177.4f, 185.93f, 178.65f, 185.82f, 180.08f, 185.01f,
        179.76f, 183.55f, 179.43f, 182.03f, 178.35f, 180.46f, 177.65f, 179.1f, 176.43f, 176.74f, 175.16f, 174.41f,
        173.92f, 172.06f, 171.85f, 168.13f, 168.78f, 164.17f, 168.04f, 159.69f, 167.89f, 158.76f, 168.05f, 157.7f,
        168.77f, 157.1f, 171.38f, 154.89f, 177.86f, 158.13f, 180.42f, 159.14f, 185.49f, 161.15f, 190.45f, 163.46f,
        195.4f, 165.74f, 201.05f, 168.34f, 206.68f, 171f, 212.42f, 173.37f, 215.89f, 174.8f, 220.11f, 176.94f,
        223.97f, 176.84f, 225.56f, 176.8f, 226.46f, 176.69f, 227.41f, 175.28f, 227.7f, 174.85f, 226.46f, 173.69f,
        226.27f, 173.44f, 226.26f, 173.44f, 226.26f, 173.44f, 226.25f, 173.44f
    };

    // On a canvas of 256 * scale, moved by (ox, oy).
    public static GraphicsPath ToPath(double scale, double ox, double oy)
    {
        var pts = new PointF[P.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF((float)(P[2 * i] * scale + ox), (float)(P[2 * i + 1] * scale + oy));
        var path = new GraphicsPath(FillMode.Winding);
        path.AddBeziers(pts);
        path.CloseFigure();
        return path;
    }

    // As SVG path data on his box, scaled and moved. (The star's box there: 28.57 to 227.45 across, 26.67 to 229.31 down.)
    public static string SvgPathData(double scale = 1, double dx = 0, double dy = 0, string digits = "0.##")
    {
        Func<int, string> pt = i => (P[2 * i] * scale + dx).ToString(digits, CultureInfo.InvariantCulture) + "," +
                                    (P[2 * i + 1] * scale + dy).ToString(digits, CultureInfo.InvariantCulture);
        var sb = new StringBuilder("M" + pt(0));
        for (int i = 1; i + 2 < P.Length / 2 + 1; i += 3) sb.Append('C').Append(pt(i)).Append(' ').Append(pt(i + 1)).Append(' ').Append(pt(i + 2));
        return sb.Append('Z').ToString();
    }
}
'@

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

# Drawn at four times the size and scaled down, so the spikes' tips stay clean at 16-24 px.
function New-IconBitmap([int]$size) {
  $n = $size * 4
  $k = $n / 512.0
  $big = New-Object System.Drawing.Bitmap -ArgumentList $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
  $g = [System.Drawing.Graphics]::FromImage($big)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.Clear([System.Drawing.Color]::Transparent)

  # Tile: flat -Tile (acid #C5F135 by default), or running to -Tile2, with a line around its edge when -Rim is given
  # (his spark's slate runs straight down; the others ran from the top left corner to the bottom right one)
  $tileBrush = if ($Tile -eq 'none') { $null } elseif ($Tile2) {
    $from = if ($Shape -eq 'spark') { New-Object System.Drawing.PointF 0, -1 } else { New-Object System.Drawing.PointF 0, 0 }
    $to = if ($Shape -eq 'spark') { New-Object System.Drawing.PointF 0, ($n + 1) } else { New-Object System.Drawing.PointF $n, $n }
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $from, $to,
      ([System.Drawing.ColorTranslator]::FromHtml($Tile)), ([System.Drawing.ColorTranslator]::FromHtml($Tile2))
    if ($TileMid) {
      $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
      $blend.Colors = [System.Drawing.Color[]]@(([System.Drawing.ColorTranslator]::FromHtml($Tile)), ([System.Drawing.ColorTranslator]::FromHtml($TileMid)), ([System.Drawing.ColorTranslator]::FromHtml($Tile2)))
      $blend.Positions = [single[]]@(0, 0.5, 1)
      $brush.InterpolationColors = $blend
    }
    $brush
  } else { New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.ColorTranslator]::FromHtml($Tile)) }
  $corner = if ($Shape -eq 'spark') { 88.96 } else { 122 }   # on the 512 canvas (his tile: 44.48 of 256)
  if ($Tile -ne 'none') { $g.FillPath($tileBrush, (New-RoundedRect 0 0 $n $n ($corner * $k))) }
  if ($Rim) {
    $rw = $RimWidth * $k
    $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.ColorTranslator]::FromHtml($Rim)), $rw
    $g.DrawPath($pen, (New-RoundedRect ($rw / 2) ($rw / 2) ($n - $rw) ($n - $rw) (122 * $k - $rw / 2)))
    $pen.Dispose()
  }

  # The mark, in -Ink (#16171B by default), larger in the small frames, where every pixel counts
  $inkBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.ColorTranslator]::FromHtml($Ink))
  if ($Shape -eq 'spark') {
    # his star where his box puts it, in every frame
    $g.FillPath($inkBrush, [IGYOSpark]::ToPath($n / 256.0, 0, 0))
  } elseif ($Shape -eq 'thorns') {
    $scale = if ($size -le 20) { $ThornFill[0] } elseif ($size -le 32) { $ThornFill[1] } elseif ($size -le 48) { $ThornFill[2] } else { $ThornFill[3] }
    $off = 256 * (1 - $scale) * $k
    $g.FillPath($inkBrush, [IGYOThorns]::ToPath($k * $scale, $off, $off))
  } elseif ($Shape -eq 'ball') {
    # the ball: one shape in every frame
    $scale = if ($size -le 20) { $BallFill[0] } elseif ($size -le 32) { $BallFill[1] } elseif ($size -le 48) { $BallFill[2] } else { $BallFill[3] }
    $off = 256 * (1 - $scale) * $k
    $ball = [IGYOBall]::ToPath($k * $scale, $off, $off, $Spikes)
    $g.FillPath($inkBrush, $ball)
    if ($Edge) {
      # the same width in every frame, in the frame's own pixels (drawn here at four times the size)
      $pen = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.ColorTranslator]::FromHtml($Edge)), ($EdgePx * 4)
      $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
      $g.DrawPath($pen, $ball)
      $pen.Dispose()
    }
  } else {
    # the star: with wider needles too in the small frames
    $scale = if ($size -le 20) { $SmallScale }
             elseif ($size -le 24) { if ($MidScale -gt 0) { $MidScale } else { ($SmallScale + 0.72) / 2 } }
             elseif ($size -le 32 -and $MidScale -gt 0) { $MidScale } else { 0.72 }
    $wide = if ($size -le 32) { $SmallBold } elseif ($size -le 48) { 1 + ($SmallBold - 1) / 2 } else { 1 }
    $pinch = if ($size -le 32) { $SmallPinch } elseif ($size -le 48) { (0.36 + $SmallPinch) / 2 } else { 0.36 }
    $off = 256 * (1 - $scale) * $k
    $g.FillPath($inkBrush, [IGYOMark]::ToPath($k * $scale, $off, $off, $wide, ($size -ge $StubsFrom), $pinch))
  }
  $g.Dispose()

  $bmp = New-Object System.Drawing.Bitmap -ArgumentList $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $edges = New-Object System.Drawing.Imaging.ImageAttributes
  $edges.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
  $g.DrawImage($big, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $n, $n, [System.Drawing.GraphicsUnit]::Pixel, $edges)
  $g.Dispose()
  $big.Dispose()
  return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
  $bmp = New-IconBitmap $size
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $images.Add($ms.ToArray())
}
New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
if ($PngOut) {
  $logo = New-IconBitmap $PngSize
  $logo.Save($PngOut, [System.Drawing.Imaging.ImageFormat]::Png)
  $logo.Dispose()
}
if ($SvgOut) {
  $d = if ($Shape -eq 'spark') { [IGYOSpark]::SvgPathData() } elseif ($Shape -eq 'thorns') { [IGYOThorns]::SvgPathData() } elseif ($Shape -eq 'ball') { [IGYOBall]::SvgPathData($Spikes) } else { [IGYOMark]::SvgPathData() }
  $box = if ($Shape -eq 'spark') { '28.57 26.67 198.89 202.64' } else { '0 0 512 512' }   # his star: its own box
  $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="' + $box + '"><path fill="' + $SvgColor + '" d="' + $d + '"/></svg>'
  [System.IO.File]::WriteAllText($SvgOut, $svg, (New-Object System.Text.UTF8Encoding $false))
}
if ($GlyphOut) {
  # the ball on a 24 x 24 icon grid, 21 units tall: one path that icons.js strokes (not saved) and fills (saved)
  # (his star: 21 of the 24 units tall, centred)
  $glyph = if ($Shape -eq 'spark') { $k = 21 / 202.64; [IGYOSpark]::SvgPathData($k, 12 - 128.01 * $k, 12 - 127.99 * $k, '0.##') }
           elseif ($Shape -eq 'thorns') { [IGYOThorns]::SvgPathData(24, 21, '0.##') } else { [IGYOBall]::SvgPathData($Spikes, 24, 21, 4, '0.##') }
  [System.IO.File]::WriteAllText($GlyphOut, $glyph, (New-Object System.Text.UTF8Encoding $false))
}
"Wrote $OutFile" + $(if ($PngOut) { " and $PngOut" } else { "" }) + $(if ($SvgOut) { " and $SvgOut" } else { "" }) + $(if ($GlyphOut) { " and $GlyphOut" } else { "" })
