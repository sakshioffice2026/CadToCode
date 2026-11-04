using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

// Program.cs registration:
// builder.Services.AddSingleton<GaPartRecognizer>();
// builder.Services.AddSingleton<IGaPartRecognizer, DefaultBomGaPartRecognizer>();

namespace CadToCode.Business.Services
{
    public sealed class DefaultBomGaPartRecognizer : IGaPartRecognizer
    {
        private const double MaxProfileMm = 8000.0;
        private const int MaxProfileEntities = 5000;
        private const double TextSearchRadiusMm = 3000.0;

        private static readonly Regex ThicknessRx = new(
            @"(?<v>\d+(?:\.\d+)?)\s*(?:mm)?\s*(?:thk|thick)|(?:thk|thickness)\.?\s*[:=\-]?\s*(?<w>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MaterialRx = new(
            @"\b(?:s\.?s\.?\s?\d{3}[a-z]?|m\.?s\.?|lms|is\s?:?\s?2062|e250)(?=\W|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DrawingInMmRx = new(
            @"\b(?:in|are\s+in)\s+mm\b|millimet",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed record DefaultRow(
            string Name,
            string[] Layers,
            string[] LayerContains,
            string[] Keywords,
            double ThicknessMm,
            string Material,
            int Qty,
            BomPartKind CoveredBy);

        private static readonly DefaultRow[] DefaultBom =
        {
            new("CASING / SCROLL", new[] { "CASINGSIDE" }, Array.Empty<string>(), new[] { "SCROLL", "CASING" }, 4.0, "MS", 1, BomPartKind.ScrollFlatPattern),
            new("BACK PLATE", new[] { "BackPlate" }, new[] { "BACKPLATE" }, new[] { "BACK PLATE", "BACKPLATE" }, 6.0, "MS", 1, BomPartKind.Unknown),
            new("SHROUD", new[] { "Shroud" }, new[] { "SHROUD" }, new[] { "SHROUD" }, 5.0, "MS", 1, BomPartKind.Unknown),
            new("BLADE", new[] { "Blade" }, new[] { "BLADE" }, new[] { "BLADE" }, 4.0, "MS", 1, BomPartKind.Unknown),
            new("HUB", Array.Empty<string>(), new[] { "HUB" }, new[] { "HUB" }, 8.0, "MS", 1, BomPartKind.Hub),
            new("SPARK PROOF RING", new[] { "RIS" }, Array.Empty<string>(), new[] { "SPARK PROOF" }, 5.0, "MS", 1, BomPartKind.Unknown),
            new("INLET SIDE PLATE", new[] { "InletSil" }, new[] { "INLETSIL" }, new[] { "INLET SIDE", "INLET SIL" }, 4.0, "MS", 1, BomPartKind.Unknown),
            new("INLET & OUTLET MATCHING FLANGE", Array.Empty<string>(), Array.Empty<string>(), new[] { "MATCHING FLANGE", "I/S FLANGE", "INLET FLANGE", "OUTLET FLANGE" }, 6.0, "MS", 1, BomPartKind.Flange),
            new("TRANSITION PIECE @ INLET", Array.Empty<string>(), new[] { "TRANSITION" }, new[] { "TRANSITION" }, 3.0, "MS", 1, BomPartKind.Unknown),
            new("FAN PEDESTAL", new[] { "PEDEND" }, Array.Empty<string>(), new[] { "PEDESTAL" }, 6.0, "MS", 1, BomPartKind.Unknown),
            new("PEDESTAL STIFFENER", new[] { "PEDSTIFF", "PEDSTIFFWV" }, Array.Empty<string>(), new[] { "STIFFENER" }, 6.0, "MS", 1, BomPartKind.Unknown),
            new("BASE FRAME", new[] { "BASEPLAN" }, Array.Empty<string>(), new[] { "BASE FRAME", "LOWER BASE FRAME" }, 6.0, "MS", 1, BomPartKind.Unknown),
            new("DRAIN PLUG & SOCKET", new[] { "DRAIN" }, new[] { "DRAIN" }, new[] { "DRAIN PLUG", "DRAIN" }, 6.0, "MS", 1, BomPartKind.Unknown),
            new("SHAFT GUARD", Array.Empty<string>(), new[] { "GUARD" }, new[] { "SHAFT GUARD" }, 2.0, "MS", 1, BomPartKind.Unknown),
            new("INLET DAMPER", Array.Empty<string>(), new[] { "DAMPER" }, new[] { "INLET DAMPER" }, 3.0, "MS", 1, BomPartKind.Unknown),
            new("INLET BIRD SCREEN", Array.Empty<string>(), new[] { "SCREEN" }, new[] { "BIRD SCREEN" }, 2.0, "MS", 1, BomPartKind.Unknown)
        };

        private readonly IGaPartRecognizer _inner;

        public DefaultBomGaPartRecognizer(GaPartRecognizer inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public IReadOnlyList<PartBomItem> Recognize(ExtractedGeometry geometry, GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(report);

            var items = _inner.Recognize(geometry, report).ToList();
            var mm = ResolveMmPerUnit(geometry);

            foreach (var row in DefaultBom)
            {
                if (IsCovered(row, items))
                {
                    continue;
                }

                if (row.CoveredBy == BomPartKind.Flange || row.CoveredBy == BomPartKind.Hub)
                {
                    report.Error(
                        "DEFAULT_BOM_NO_GEOMETRY",
                        row.Name,
                        "Default BOM row has no recognisable circular geometry in the GA. No DXF was generated.");
                    continue;
                }

                var profile = CollectProfile(geometry, row, mm, out var minX, out var minY, out var maxX, out var maxY);

                if (profile.Count == 0)
                {
                    report.Error(
                        "DEFAULT_BOM_NO_GEOMETRY",
                        row.Name,
                        "Default BOM row has no geometry in layers or layer names matching this part. No DXF was generated.");
                    continue;
                }

                var width = maxX - minX;
                var height = maxY - minY;

                if (profile.Count > MaxProfileEntities || Math.Max(width, height) > MaxProfileMm)
                {
                    report.Error(
                        "DEFAULT_BOM_GEOMETRY_TOO_LARGE",
                        row.Name,
                        profile.Count.ToString(CultureInfo.InvariantCulture) + " entities, "
                        + Math.Round(Math.Max(width, height), 0).ToString(CultureInfo.InvariantCulture)
                        + " mm extent. No DXF was generated.");
                    continue;
                }

                var item = new PartBomItem
                {
                    Name = row.Name,
                    Kind = BomPartKind.ProfilePlate,
                    LengthMm = Math.Round(Math.Max(width, height), 2),
                    WidthMm = Math.Round(Math.Min(width, height), 2),
                    Qty = row.Qty,
                    Profile = profile,
                    Remarks = "Default BOM row; outline taken from GA geometry."
                };

                var centre = FindLabelPosition(geometry, row) ?? new GeometryPoint((minX + maxX) / 2.0 / mm, (minY + maxY) / 2.0 / mm);
                var radius = TextSearchRadiusMm / mm;

                var thickness = NearestThickness(geometry, centre, radius);
                if (thickness.HasValue)
                {
                    item.ThicknessMm = thickness;
                }
                else
                {
                    item.ThicknessMm = row.ThicknessMm;
                    report.Warn(
                        "DEFAULT_BOM_THICKNESS",
                        row.Name,
                        "Thickness not found near this part in the GA. Default " + row.ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm used. Please verify.");
                }

                var material = NearestMaterial(geometry, centre, radius);
                if (material is not null)
                {
                    item.Material = material;
                }
                else
                {
                    item.Material = row.Material;
                    report.Warn(
                        "DEFAULT_BOM_MATERIAL",
                        row.Name,
                        "Material not found near this part in the GA. Default " + row.Material + " used. Please verify.");
                }

                report.Info(
                    "DEFAULT_BOM_ROW",
                    row.Name,
                    "Added from default BOM: " + profile.Count.ToString(CultureInfo.InvariantCulture) + " entities, "
                    + item.LengthMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " x "
                    + item.WidthMm.Value.ToString("0.#", CultureInfo.InvariantCulture)
                    + " mm, THK " + item.ThicknessMm.Value.ToString("0.##", CultureInfo.InvariantCulture)
                    + ", MAT " + item.Material + ", QTY " + item.Qty.ToString(CultureInfo.InvariantCulture) + ".");

                items.Add(item);
            }

            var number = 0;
            foreach (var item in items)
            {
                number++;
                item.ItemNo = number;
            }

            return items;
        }

        private static bool IsCovered(DefaultRow row, List<PartBomItem> items)
        {
            if (row.CoveredBy != BomPartKind.Unknown && items.Any(i => i.Kind == row.CoveredBy))
            {
                return true;
            }

            return items.Any(i => row.Keywords.Any(k => i.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool LayerMatches(string? layer, DefaultRow row)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            if (row.Layers.Any(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return row.LayerContains.Any(k => layer.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ProfileEntity> CollectProfile(
            ExtractedGeometry g,
            DefaultRow row,
            double mm,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            var list = new List<ProfileEntity>();

            foreach (var l in g.Lines.Where(l => LayerMatches(l.Layer, row)))
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Line, l.Start.X * mm, l.Start.Y * mm, l.End.X * mm, l.End.Y * mm, 0, 0, 0, 0, 0));
            }

            foreach (var a in g.Arcs.Where(a => LayerMatches(a.Layer, row)))
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Arc, 0, 0, 0, 0, a.Center.X * mm, a.Center.Y * mm, a.Radius * mm, a.StartAngle, a.EndAngle));
            }

            foreach (var c in g.Circles.Where(c => LayerMatches(c.Layer, row)))
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Circle, 0, 0, 0, 0, c.Center.X * mm, c.Center.Y * mm, c.Radius * mm, 0, 360));
            }

            foreach (var p in g.Polylines.Where(p => LayerMatches(p.Layer, row)))
            {
                var count = p.Vertices.Count;
                var segments = p.IsClosed ? count : count - 1;

                for (var i = 0; i < segments; i++)
                {
                    var v0 = p.Vertices[i];
                    var v1 = p.Vertices[(i + 1) % count];
                    AddSegment(list, v0.X * mm, v0.Y * mm, v1.X * mm, v1.Y * mm, v0.Bulge);
                }
            }

            var lx0 = double.MaxValue;
            var ly0 = double.MaxValue;
            var lx1 = double.MinValue;
            var ly1 = double.MinValue;

            foreach (var e in list)
            {
                double ax;
                double ay;
                double bx;
                double by;

                if (e.Kind == ProfileEntityKind.Line)
                {
                    ax = Math.Min(e.X1, e.X2);
                    ay = Math.Min(e.Y1, e.Y2);
                    bx = Math.Max(e.X1, e.X2);
                    by = Math.Max(e.Y1, e.Y2);
                }
                else
                {
                    ax = e.Cx - e.Radius;
                    ay = e.Cy - e.Radius;
                    bx = e.Cx + e.Radius;
                    by = e.Cy + e.Radius;
                }

                lx0 = Math.Min(lx0, ax);
                ly0 = Math.Min(ly0, ay);
                lx1 = Math.Max(lx1, bx);
                ly1 = Math.Max(ly1, by);
            }

            if (list.Count == 0)
            {
                lx0 = 0.0;
                ly0 = 0.0;
                lx1 = 0.0;
                ly1 = 0.0;
            }

            minX = lx0;
            minY = ly0;
            maxX = lx1;
            maxY = ly1;

            return list;
        }

        private static void AddSegment(List<ProfileEntity> list, double x0, double y0, double x1, double y1, double bulge)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            var chord = Math.Sqrt(dx * dx + dy * dy);

            if (chord <= 1e-9)
            {
                return;
            }

            if (Math.Abs(bulge) < 1e-9)
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Line, x0, y0, x1, y1, 0, 0, 0, 0, 0));
                return;
            }

            var sweep = 4.0 * Math.Atan(Math.Abs(bulge));
            var radius = chord / (2.0 * Math.Sin(sweep / 2.0));
            var offset = radius * Math.Cos(sweep / 2.0);
            var side = bulge > 0 ? 1.0 : -1.0;

            var cx = (x0 + x1) / 2.0 + side * (-dy / chord) * offset;
            var cy = (y0 + y1) / 2.0 + side * (dx / chord) * offset;

            var a0 = Math.Atan2(y0 - cy, x0 - cx) * 180.0 / Math.PI;
            var a1 = Math.Atan2(y1 - cy, x1 - cx) * 180.0 / Math.PI;
            var start = bulge > 0 ? a0 : a1;
            var end = bulge > 0 ? a1 : a0;

            list.Add(new ProfileEntity(
                ProfileEntityKind.Arc, 0, 0, 0, 0, cx, cy, radius,
                (start % 360.0 + 360.0) % 360.0,
                (end % 360.0 + 360.0) % 360.0));
        }

        private static GeometryPoint? FindLabelPosition(ExtractedGeometry g, DefaultRow row)
        {
            foreach (var text in g.Texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text))
                {
                    continue;
                }

                if (row.Keywords.Any(k => text.Text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return text.Position;
                }
            }

            return null;
        }

        private static double? NearestThickness(ExtractedGeometry g, GeometryPoint origin, double radius)
        {
            GeometryText? best = null;
            Match? bestMatch = null;
            var bestDistance = double.MaxValue;

            foreach (var text in g.Texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text))
                {
                    continue;
                }

                var match = ThicknessRx.Match(text.Text);
                if (!match.Success)
                {
                    continue;
                }

                var d = Distance(text.Position, origin);
                if (d <= radius && d < bestDistance)
                {
                    best = text;
                    bestMatch = match;
                    bestDistance = d;
                }
            }

            if (best is null || bestMatch is null)
            {
                return null;
            }

            var raw = bestMatch.Groups["v"].Success ? bestMatch.Groups["v"].Value : bestMatch.Groups["w"].Value;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        }

        private static string? NearestMaterial(ExtractedGeometry g, GeometryPoint origin, double radius)
        {
            Match? bestMatch = null;
            var bestDistance = double.MaxValue;

            foreach (var text in g.Texts)
            {
                if (string.IsNullOrWhiteSpace(text.Text))
                {
                    continue;
                }

                var match = MaterialRx.Match(text.Text);
                if (!match.Success)
                {
                    continue;
                }

                var d = Distance(text.Position, origin);
                if (d <= radius && d < bestDistance)
                {
                    bestMatch = match;
                    bestDistance = d;
                }
            }

            return bestMatch is null
                ? null
                : bestMatch.Value.Replace(".", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private static double Distance(GeometryPoint a, GeometryPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double MmPerUnit(DrawingUnit unit)
        {
            return unit switch
            {
                DrawingUnit.Centimeter => 10.0,
                DrawingUnit.Meter => 1000.0,
                DrawingUnit.Inch => 25.4,
                DrawingUnit.Foot => 304.8,
                _ => 1.0
            };
        }

        private static double ResolveMmPerUnit(ExtractedGeometry geometry)
        {
            var declared = MmPerUnit(geometry.DetectedUnit);

            if (Math.Abs(declared - 1.0) <= 1e-9)
            {
                return declared;
            }

            var mentionsMm = geometry.Texts.Any(t => !string.IsNullOrEmpty(t.Text) && DrawingInMmRx.IsMatch(t.Text));
            var extent = geometry.Bounds is null ? 0.0 : Math.Max(geometry.Bounds.Width, geometry.Bounds.Height);
            var implausiblyLarge = extent <= 60000.0 && extent * declared > 60000.0;

            return mentionsMm || implausiblyLarge ? 1.0 : declared;
        }
    }
}