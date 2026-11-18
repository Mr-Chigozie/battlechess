// The chess symbols that float over the units, drawn from the same vector shapes as the web game's badges
// (js/stage3d.js SHAPES), rasterised in code: no font glyphs, which some phones turn into colour emoji.
// A tiny path filler (non-zero winding, 4x4 supersampled) turns the shapes into 128 px textures once.
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public static class BadgeArt
    {
        const int Size = 128;
        static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        // A path in the web canvas's 100 x 100 design space (y down), flattened into closed polygons.
        sealed class Path
        {
            public readonly List<List<Vector2>> Contours = new List<List<Vector2>>();
            List<Vector2> current;
            Vector2 pen;

            public void MoveTo(float x, float y) { current = new List<Vector2> { new Vector2(x, y) }; Contours.Add(current); pen = new Vector2(x, y); }

            public void LineTo(float x, float y)
            {
                if (current == null) MoveTo(pen.x, pen.y);
                current.Add(new Vector2(x, y));
                pen = new Vector2(x, y);
            }

            public void BezierTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
            {
                var p0 = pen;
                var c1 = new Vector2(c1x, c1y);
                var c2 = new Vector2(c2x, c2y);
                var p3 = new Vector2(x, y);
                for (int i = 1; i <= 16; i++)
                {
                    float t = i / 16f, u = 1 - t;
                    var p = u * u * u * p0 + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * p3;
                    LineTo(p.x, p.y);
                }
            }

            // A full circle; like canvas arc(), it joins the current subpath (or starts one).
            public void Circle(float cx, float cy, float r)
            {
                if (current == null) MoveTo(cx + r, cy);
                else LineTo(cx + r, cy);
                for (int i = 1; i <= 32; i++)
                {
                    float a = i * Mathf.PI * 2 / 32;
                    LineTo(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
                }
            }

            public void Rect(float x, float y, float w, float h)
            {
                MoveTo(x, y); LineTo(x + w, y); LineTo(x + w, y + h); LineTo(x, y + h);
                current = null;
                pen = new Vector2(x, y);
            }
        }

        static void Shape(Path g, char type)
        {
            switch (type)
            {
                case 'p':
                    g.Circle(50, 34, 13); g.MoveTo(40, 48); g.LineTo(60, 48); g.LineTo(66, 76); g.LineTo(34, 76);
                    break;
                case 'r':
                    g.MoveTo(30, 20); g.LineTo(38, 20); g.LineTo(38, 27); g.LineTo(46, 27); g.LineTo(46, 20); g.LineTo(54, 20); g.LineTo(54, 27);
                    g.LineTo(62, 27); g.LineTo(62, 20); g.LineTo(70, 20); g.LineTo(70, 36); g.LineTo(64, 40); g.LineTo(66, 76); g.LineTo(34, 76);
                    g.LineTo(36, 40); g.LineTo(30, 36);
                    break;
                case 'b':
                    g.Circle(50, 15, 5);
                    g.MoveTo(50, 21); g.BezierTo(68, 32, 66, 50, 58, 56); g.LineTo(64, 76); g.LineTo(36, 76); g.LineTo(42, 56);
                    g.BezierTo(34, 50, 32, 32, 50, 21);
                    break;
                case 'n':
                    g.MoveTo(36, 76); g.LineTo(38, 58); g.BezierTo(30, 54, 24, 50, 26, 44); g.LineTo(44, 34); g.LineTo(40, 22);
                    g.BezierTo(52, 16, 70, 22, 70, 44); g.LineTo(66, 76);
                    break;
                case 'q':
                    foreach (var (x, y) in new[] { (26f, 22f), (38f, 16f), (50f, 13f), (62f, 16f), (74f, 22f) }) { g.MoveTo(x + 5, y); g.Circle(x, y, 5); }
                    g.MoveTo(26, 28); g.LineTo(38, 46); g.LineTo(38, 22); g.LineTo(50, 44); g.LineTo(62, 22); g.LineTo(62, 46); g.LineTo(74, 28);
                    g.LineTo(64, 58); g.LineTo(66, 76); g.LineTo(34, 76); g.LineTo(36, 58);
                    break;
                case 'k':
                    g.Rect(46, 8, 8, 22); g.Rect(39, 14, 22, 7);
                    g.MoveTo(50, 32); g.BezierTo(74, 30, 76, 50, 62, 58); g.LineTo(66, 76); g.LineTo(34, 76); g.LineTo(38, 58);
                    g.BezierTo(24, 50, 26, 30, 50, 32);
                    break;
            }
            g.Rect(30, 76, 40, 9);   // the plinth every piece stands on
        }

        /// <summary>The badge for a side ('w' ivory disc, dark figure; 'b' the reverse) and piece type.</summary>
        public static Sprite Get(char color, char type)
        {
            string key = $"{color}{type}";
            if (Cache.TryGetValue(key, out var sprite)) return sprite;
            bool ivory = color == 'w';
            var disc = ivory ? Armies.Hex(0xf3ead2) : Armies.Hex(0x17181c);
            var figure = ivory ? Armies.Hex(0x1b1c20) : Armies.Hex(0xf3ead2);
            var gold = Armies.Hex(0xd6ad66);

            var ring = new Path(); ring.Circle(50, 50, 48.5f);
            var face = new Path(); face.Circle(50, 50, 43.5f);
            var piece = new Path(); Shape(piece, type);
            // Shrink the symbol a little inside the disc, as the web does: translate(50,52) scale(.82) translate(-50,-50).
            foreach (var c in piece.Contours)
                for (int i = 0; i < c.Count; i++) c[i] = (c[i] - new Vector2(50, 50)) * 0.82f + new Vector2(50, 52);

            var pixels = new Color32[Size * Size];
            Paint(pixels, ring, gold);
            Paint(pixels, face, disc);
            Paint(pixels, piece, figure);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            sprite = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
            Cache[key] = sprite;
            return sprite;
        }

        // Fills a path over the pixels (alpha-blended), non-zero winding, 4x4 samples per pixel.
        static void Paint(Color32[] pixels, Path path, Color color)
        {
            const int S = 4;
            float scale = Size / 100f;
            var edges = new List<(Vector2 a, Vector2 b)>();
            foreach (var c in path.Contours)
                for (int i = 0; i < c.Count; i++)
                    edges.Add((c[i] * scale, c[(i + 1) % c.Count] * scale));
            var coverage = new float[Size * Size];
            var crossings = new List<(float x, int dir)>();
            for (int sy = 0; sy < Size * S; sy++)
            {
                float y = (sy + 0.5f) / S;
                crossings.Clear();
                foreach (var (a, b) in edges)
                {
                    if ((a.y <= y) == (b.y <= y)) continue;
                    float x = a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x);
                    crossings.Add((x, b.y > a.y ? 1 : -1));
                }
                crossings.Sort((p, q) => p.x.CompareTo(q.x));
                int winding = 0;
                for (int i = 0; i < crossings.Count - 1; i++)
                {
                    winding += crossings[i].dir;
                    if (winding == 0) continue;
                    // Inside from crossings[i].x to crossings[i + 1].x: add coverage in sub-sample steps.
                    int x0 = Mathf.Max(0, Mathf.CeilToInt(crossings[i].x * S - 0.5f));
                    int x1 = Mathf.Min(Size * S - 1, Mathf.FloorToInt(crossings[i + 1].x * S - 0.5f));
                    int py = Size - 1 - sy / S;   // canvas y runs down, textures up
                    for (int sx = x0; sx <= x1; sx++) coverage[py * Size + sx / S] += 1f / (S * S);
                }
            }
            for (int i = 0; i < pixels.Length; i++)
            {
                float a = Mathf.Clamp01(coverage[i]) * color.a;
                if (a <= 0) continue;
                Color dst = pixels[i];
                float outA = a + dst.a * (1 - a);
                var rgb = (new Color(color.r, color.g, color.b) * a + new Color(dst.r, dst.g, dst.b) * dst.a * (1 - a)) / Mathf.Max(outA, 1e-4f);
                pixels[i] = new Color(rgb.r, rgb.g, rgb.b, outA);
            }
        }
    }
}
