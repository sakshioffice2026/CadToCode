using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Business.Interfaces;
using CadToCode.Business.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;

namespace CadToCode.Business.Interfaces
{
    public interface IGaPartRecognizer
    {
        IReadOnlyList<PartBomItem> Recognize(ExtractedGeometry geometry, GenerationReport report);
    }
}

namespace CadToCode.Business.Services
{
    /// <summary>
    /// Turns a General Arrangement drawing into BOM rows from real geometry (concentric circles grouped by centre,
    /// labelled rectangles) and drawing text (THK, material, quantity).
    /// Nothing is invented: a value that cannot be read from the drawing stays null and is reported by the
    /// parametric rules engine. Shapes that cannot be recognised are reported, never silently dropped.
    /// </summary>
    public sealed class GaPartRecognizer : IGaPartRecognizer
    {
        private const double MinPartMm = 50.0;
        private const double MinPlateMm = 20.0;
        private const double MinCircularMm = 100.0;
        private const double MinRingWallMm = 5.0;
        private const int MaxIdsInMessage = 20;

        private static readonly HashSet<string> IgnoredLayers = new(StringComparer.OrdinalIgnoreCase) { "PAPER", "DEFPOINTS" };

        private static readonly Regex ThicknessA = new(
            @"(?<v>\d+(?:\.\d+)?)\s*mm\s*(?:thk|thick)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ThicknessB = new(
            @"(?:thk|thickness)\.?\s*[:=\-]?\s*(?<v>\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QuantityRx = new(
            @"qty\.?\s*[:=\-]?\s*(?<v>\d{1,3})|(?<w>\d{1,3})\s*(?:nos?|off)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MaterialRx = new(
            @"\b(?:m\.?s\.?|ss\s?\d{3}[a-z]?|is\s?2062|e250)(?=\W|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PlateHintRx = new(
            @"plate|base|bracket|support|stiffener|flat",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NumericOnlyRx = new(
            @"^[\s\d.,:xX×Ø\-]+$",
            RegexOptions.Compiled);

        private readonly IComponentDecomposer _decomposer;
        private const int MaxProfileEntities = 600;
        private const double MaxProfileMm = 5000.0;
        private static readonly Regex DrawingInMmRx = new(
                    @"\b(?:in|are\s+in)\s+mm\b|millimet",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static bool ContainsRecognisedRing(AssemblyNode node, double mm, IReadOnlyList<PartBomItem> items)
        {
            var diameters = node.Geometry.Circles.Select(c => c.Radius * 2.0 * mm).ToList();

            return items.Any(i => i.Kind == BomPartKind.Flange
                && diameters.Any(d =>
                    (i.OuterDiameterMm.HasValue && Math.Abs(d - i.OuterDiameterMm.Value) <= 0.5)
                    || (i.InnerDiameterMm.HasValue && Math.Abs(d - i.InnerDiameterMm.Value) <= 0.5)));
        }

        private static bool TryAnalyseProfile(AssemblyNode node, double mm, out PartBomItem item)
        {
            item = null!;

            var width = node.Bounds.Width * mm;
            var height = node.Bounds.Height * mm;
            if (Math.Max(width, height) > MaxProfileMm || Math.Min(width, height) < MinPlateMm)
            {
                return false;
            }

            var profile = ToProfile(node.Geometry, mm);
            if (profile.Count == 0 || profile.Count > MaxProfileEntities)
            {
                return false;
            }

            item = new PartBomItem
            {
                Kind = BomPartKind.ProfilePlate,
                LengthMm = Math.Round(Math.Max(width, height), 2),
                WidthMm = Math.Round(Math.Min(width, height), 2),
                Profile = profile
            };

            return true;
        }

        private static List<ProfileEntity> ToProfile(ExtractedGeometry g, double mm)
        {
            var list = new List<ProfileEntity>();

            foreach (var l in g.Lines)
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Line, l.Start.X * mm, l.Start.Y * mm, l.End.X * mm, l.End.Y * mm, 0, 0, 0, 0, 0));
            }

            foreach (var a in g.Arcs)
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Arc, 0, 0, 0, 0, a.Center.X * mm, a.Center.Y * mm, a.Radius * mm, a.StartAngle, a.EndAngle));
            }

            foreach (var c in g.Circles)
            {
                list.Add(new ProfileEntity(ProfileEntityKind.Circle, 0, 0, 0, 0, c.Center.X * mm, c.Center.Y * mm, c.Radius * mm, 0, 360));
            }

            foreach (var p in g.Polylines)
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
        public GaPartRecognizer(IComponentDecomposer decomposer)
        {
            _decomposer = decomposer ?? throw new ArgumentNullException(nameof(decomposer));
        }

        public IReadOnlyList<PartBomItem> Recognize(ExtractedGeometry geometry, GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(geometry);
            ArgumentNullException.ThrowIfNull(report);

            var items = new List<PartBomItem>();
            var nodes = _decomposer.Decompose(geometry);
            var mm = ResolveMmPerUnit(geometry);
            var bySignature = new Dictionary<string, PartBomItem>(StringComparer.Ordinal);
            var textQuantity = new HashSet<PartBomItem>();
            var unrecognised = new List<string>();

            void Add(PartBomItem item, string text, string? name, string source)
            {
                var thickness = ReadThickness(text);
                if (thickness.HasValue)
                {
                    item.ThicknessMm = thickness;
                }

                var material = ReadMaterial(text);
                if (material is not null)
                {
                    item.Material = material;
                }

                var quantity = ReadQuantity(text);
                var signature = Signature(item);

                if (bySignature.TryGetValue(signature, out var existing))
                {
                    if (!textQuantity.Contains(existing))
                    {
                        existing.Qty++;
                    }

                    existing.ThicknessMm ??= item.ThicknessMm;
                    existing.Material ??= item.Material;
                    return;
                }

                item.Name = string.IsNullOrWhiteSpace(name) ? DefaultName(item) : name;
                item.Remarks = "Recognised from GA geometry (" + source + ").";

                if (quantity.HasValue)
                {
                    item.Qty = quantity.Value;
                    textQuantity.Add(item);
                }

                bySignature[signature] = item;
                items.Add(item);
            }

            foreach (var ring in RecognizeCircularGroups(geometry, mm))
            {
                Add(ring, string.Empty, null, "concentric circles");
            }

            var scroll = RecognizeScroll(geometry, mm, report);
            if (scroll is not null)
            {
                Add(scroll, string.Empty, "SCROLL", "CASINGSIDE arcs and CASINGWIDTH plates");
            }

            foreach (var node in nodes)
            {
                var text = string.Join(" ", node.Labels ?? new List<string>());

                if (TryAnalysePlate(node, mm, text, out var plate))
                {
                    Add(plate, text, ResolveName(node, plate), "node " + node.Id);
                    continue;
                }

                if (node.HasClosedProfile && Math.Max(node.Bounds.Width, node.Bounds.Height) * mm >= MinPartMm)
                {
                    if (ContainsRecognisedRing(node, mm, items))
                    {
                        continue;
                    }

                    if ((ReadThickness(text).HasValue || ReadQuantity(text).HasValue) && TryAnalyseProfile(node, mm, out var profilePart))
                    {
                        Add(profilePart, text, ResolveName(node, profilePart), "outline of node " + node.Id);
                        continue;
                    }

                    unrecognised.Add(node.Id);
                }
            }
            
            var number = 0;
            foreach (var item in items)
            {
                number++;
                item.ItemNo = number;
                report.Info("GA_RECOGNISED", item.DisplayName, DescribeItem(item));


                if (!textQuantity.Contains(item))
                {
                    report.Warn(
                        "QUANTITY_NOT_IN_DRAWING",
                        item.DisplayName,
                        "No quantity label was found. " + item.Qty.ToString(CultureInfo.InvariantCulture) + " was counted from geometry. Please verify.");
                }
            }

            if (unrecognised.Count > 0)
            {
                var shown = string.Join(", ", unrecognised.Take(MaxIdsInMessage));
                var more = unrecognised.Count > MaxIdsInMessage
                    ? " and " + (unrecognised.Count - MaxIdsInMessage).ToString(CultureInfo.InvariantCulture) + " more"
                    : string.Empty;

                report.Warn(
                    "GA_NOT_RECOGNISED",
                    "GA",
                    unrecognised.Count.ToString(CultureInfo.InvariantCulture)
                    + " closed shapes were not recognised as manufacturable parts (" + shown + more + "). Add them to the BOM or review them.");
            }

            if (items.Count == 0)
            {
                report.Error("GA_NO_PARTS", "GA", "No circular part or labelled plate could be recognised from this drawing.");
            }

            return items;
        }
        private static PartBomItem? RecognizeScroll(ExtractedGeometry geometry, double mm, GenerationReport report)
        {
            var radii = geometry.Arcs
                .Where(a => string.Equals(a.Layer, "CASINGSIDE", StringComparison.OrdinalIgnoreCase) && a.Radius * mm >= 300.0)
                .Select(a => Math.Round(a.Radius * mm, 2))
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            var spiral = new List<double>();
            foreach (var r in radii)
            {
                if (spiral.Count == 0 || r - spiral[^1] > 12.0)
                {
                    spiral.Add(r);
                }
            }

            if (spiral.Count < 3)
            {
                var casingArcs = geometry.Arcs.Count(a => string.Equals(a.Layer, "CASINGSIDE", StringComparison.OrdinalIgnoreCase));
                report.Warn(
                    "GA_SCROLL_NOT_FOUND",
                    "SCROLL",
                    "CASINGSIDE arcs: " + casingArcs.ToString(CultureInfo.InvariantCulture)
                    + ", arcs of R 300 mm or more: " + radii.Count.ToString(CultureInfo.InvariantCulture)
                    + ", all arcs: " + geometry.Arcs.Count.ToString(CultureInfo.InvariantCulture)
                    + ", mm per unit: " + mm.ToString("0.###", CultureInfo.InvariantCulture)
                    + ", layers: " + string.Join(",", geometry.Layers.Take(40)));
                return null;
            }

            var steps = spiral.Zip(spiral.Skip(1), (a, b) => b - a).ToList();
            var meanStep = steps.Average();
            if (steps.Any(s => Math.Abs(s - meanStep) > meanStep * 0.05))
            {
                report.Warn(
                    "GA_SCROLL_NOT_FOUND",
                    "SCROLL",
                    "Arc radii are not evenly stepped: " + string.Join(", ", spiral.Select(r => Format(r))));
                return null;
            }

          

            var minLength = spiral[^1] * 2.0 * 0.9;
            var xs = geometry.Lines
                .Where(l => string.Equals(l.Layer, "CASINGWIDTH", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(l.Start.X - l.End.X) * mm <= 0.05
                    && l.Length * mm >= minLength)
                .Select(l => Math.Round(l.Start.X * mm, 1))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var plates = new List<(double From, double To)>();
            for (var i = 0; i < xs.Count - 1; i++)
            {
                if (xs[i + 1] - xs[i] <= 12.0)
                {
                    plates.Add((xs[i], xs[i + 1]));
                    i++;
                }
            }

            double? width = null;
            double? thickness = null;
            if (plates.Count >= 2
                && Math.Abs((plates[0].To - plates[0].From) - (plates[1].To - plates[1].From)) < 0.1)
            {
                thickness = Math.Round(plates[0].To - plates[0].From, 2);
                width = Math.Round(plates[1].From - plates[0].To, 2);
            }

            report.Info(
                "GA_SCROLL_MEASURED",
                "SCROLL",
                "Start R " + Format(spiral[0]) + ", end R " + Format(spiral[^1])
                + ", width " + Format(width) + ", plate thickness " + Format(thickness)
                + ". Measured from the CASINGSIDE arcs and CASINGWIDTH plates. Please verify.");

            return new PartBomItem
            {
                Kind = BomPartKind.ScrollFlatPattern,
                SmallDiameterMm = Math.Round(spiral[0] * 2.0, 2),
                LargeDiameterMm = Math.Round(spiral[^1] * 2.0, 2),
                WidthMm = width,
                ThicknessMm = thickness
            };
        }
        private static List<PartBomItem> RecognizeCircularGroups(ExtractedGeometry geometry, double mm)
        {
            var result = new List<PartBomItem>();

            var circles = geometry.Circles
            .Where(c => c.Radius > 0 && !IgnoredLayers.Contains(c.Layer ?? string.Empty))
            .Select(c => (X: c.Center.X * mm, Y: c.Center.Y * mm, D: c.Radius * 2.0 * mm, IsCentreLine: IsCentreLineType(c.LineType)))
            .ToList();

            var used = new bool[circles.Count];

            for (var i = 0; i < circles.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var cx = circles[i].X;
                var cy = circles[i].Y;
                var diameters = new List<double>();
                var centreDiameters = new List<double>();

                for (var j = i; j < circles.Count; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    if (Distance(circles[j].X, circles[j].Y, cx, cy) <= Math.Max(0.5, circles[j].D * 0.002))
                    {
                        used[j] = true;
                        if (circles[j].IsCentreLine)
                        {
                            centreDiameters.Add(circles[j].D);
                        }
                        else
                        {
                            diameters.Add(circles[j].D);
                        }
                    }
                }

                var distinct = DistinctDescending(diameters);
                var distinctCentre = DistinctDescending(centreDiameters);

                if (distinct.Count < 2 || distinct[0] < MinCircularMm)
                {
                    continue;
                }

                result.AddRange(PeelRings(distinct, distinctCentre));
            }

            return result;
        }

        private static List<PartBomItem> PeelRings(List<double> diameters, List<double> centreDiameters)
        {
            var rings = new List<PartBomItem>();
            var rest = new List<double>(diameters);

            var centreRest = new List<double>(centreDiameters);

            while (rest.Count >= 2)
            {
                var outer = rest[0];
                var inner = rest[1];
                double? pcd = null;
                var removed = new List<int> { 0, 1 };

                for (var k = 1; k < rest.Count - 1 && pcd is null; k++)
                {
                    for (var m = k + 1; m < rest.Count; m++)
                    {
                        if (Math.Abs(rest[k] - (outer + rest[m]) / 2.0) <= outer * 0.005)
                        {
                            pcd = rest[k];
                            inner = rest[m];
                            removed = new List<int> { 0, k, m };
                            break;
                        }
                    }
                }

                foreach (var index in removed.OrderByDescending(r => r))
                {
                    rest.RemoveAt(index);
                }

                if ((outer - inner) / 2.0 < MinRingWallMm)
                {
                    continue;
                }

                rings.Add(new PartBomItem
                {
                    Kind = BomPartKind.Flange,
                    OuterDiameterMm = Math.Round(outer, 2),
                    InnerDiameterMm = Math.Round(inner, 2),
                    HolePcdMm = pcd.HasValue ? Math.Round(pcd.Value, 2) : null
                });
            }

            return rings;
        }

        private static List<double> DistinctDescending(IEnumerable<double> values)
        {
            var distinct = new List<double>();
            foreach (var d in values.OrderByDescending(d => d))
            {
                if (distinct.Count == 0 || Math.Abs(distinct[^1] - d) > 0.05)
                {
                    distinct.Add(d);
                }
            }

            return distinct;
        }

        private static bool IsCentreLineType(string? lineType)
        {
            return !string.IsNullOrEmpty(lineType)
                && (lineType.Contains("CENTER", StringComparison.OrdinalIgnoreCase)
                    || lineType.Contains("CENTRE", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryAnalysePlate(AssemblyNode node, double mm, string text, out PartBomItem item)
        {
            item = null!;

            if (node.Shape != PartShape.Rectangular || !node.HasClosedProfile || !PlateHintRx.IsMatch(text))
            {
                return false;
            }

            var w = node.Bounds.Width * mm;
            var h = node.Bounds.Height * mm;
            var length = Math.Max(w, h);
            var width = Math.Min(w, h);

            if (width < MinPlateMm)
            {
                return false;
            }

            item = new PartBomItem
            {
                Kind = BomPartKind.RectangularPlate,
                LengthMm = Math.Round(length, 2),
                WidthMm = Math.Round(width, 2)
            };

            return true;
        }

        private static string DefaultName(PartBomItem item)
        {
            return item.Kind == BomPartKind.Flange
                ? "FLANGE OD" + Format(item.OuterDiameterMm) + " ID" + Format(item.InnerDiameterMm)
                : "PART";
        }

        private static string ResolveName(AssemblyNode node, PartBomItem item)
        {
            var label = (node.Labels ?? new List<string>())
                .Select(l => l?.Trim() ?? string.Empty)
                .FirstOrDefault(l => l.Length is > 2 and <= 40
                    && !NumericOnlyRx.IsMatch(l)
                    && !ThicknessA.IsMatch(l)
                    && !ThicknessB.IsMatch(l)
                    && !QuantityRx.IsMatch(l));

            if (!string.IsNullOrWhiteSpace(label))
            {
                return label.ToUpperInvariant();
            }

            return item.Kind switch
            {
                BomPartKind.Flange => DefaultName(item),
                BomPartKind.RectangularPlate => "PLATE " + Format(item.LengthMm) + " X " + Format(item.WidthMm),
                _ => node.Name
            };
        }

        private static string DescribeItem(PartBomItem item)
        {
            var parts = new List<string> { item.Kind.ToString() };

            if (item.OuterDiameterMm.HasValue) parts.Add("OD " + Format(item.OuterDiameterMm));
            if (item.InnerDiameterMm.HasValue) parts.Add("ID " + Format(item.InnerDiameterMm));
            if (item.LengthMm.HasValue) parts.Add("L " + Format(item.LengthMm));
            if (item.WidthMm.HasValue) parts.Add("W " + Format(item.WidthMm));
            if (item.HolePcdMm.HasValue) parts.Add("PCD " + Format(item.HolePcdMm));
            if (item.HoleCount.HasValue) parts.Add(item.HoleCount.Value.ToString(CultureInfo.InvariantCulture) + " holes D" + Format(item.HoleDiameterMm));
            parts.Add("THK " + (item.ThicknessMm.HasValue ? Format(item.ThicknessMm) : "not in drawing"));
            parts.Add("MAT " + (item.Material ?? "not in drawing"));
            parts.Add("QTY " + item.Qty.ToString(CultureInfo.InvariantCulture));

            return string.Join(", ", parts);
        }

        private static string Signature(PartBomItem item)
        {
            static string R(double? v) => v.HasValue ? Math.Round(v.Value, 0).ToString("0", CultureInfo.InvariantCulture) : "-";

            return string.Join("|",
                item.Kind,
                R(item.OuterDiameterMm),
                R(item.InnerDiameterMm),
                R(item.LengthMm),
                R(item.WidthMm),
                item.HoleCount?.ToString(CultureInfo.InvariantCulture) ?? "-",
                R(item.HoleDiameterMm),
                R(item.HolePcdMm));
        }

        private static double? ReadThickness(string text)
        {
            foreach (var rx in new[] { ThicknessA, ThicknessB })
            {
                var match = rx.Match(text);
                if (match.Success
                    && double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value > 0)
                {
                    return value;
                }
            }

            return null;
        }

        private static string? ReadMaterial(string text)
        {
            var match = MaterialRx.Match(text);
            if (!match.Success)
            {
                return null;
            }

            return match.Value.Replace(".", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private static int? ReadQuantity(string text)
        {
            var match = QuantityRx.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var raw = match.Groups["v"].Success ? match.Groups["v"].Value : match.Groups["w"].Value;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
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
        private static double Distance(double x0, double y0, double x1, double y1)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Format(double? value)
        {
            return value.HasValue ? Math.Round(value.Value, 1).ToString("0.#", CultureInfo.InvariantCulture) : "-";
        }
    }
}