using System.Globalization;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaHolePattern
    {
        public int Count { get; init; }

        public double DiameterMm { get; init; }

        public double? PcdMm { get; init; }

        public bool Radial => PcdMm.HasValue;
    }

    public sealed class GaProfileResult
    {
        public GaPartClassification Classification { get; init; } = new();

        public PartBomItem? Item { get; set; }

        public List<ProfileEntity> Entities { get; } = new();

        public List<GaHolePattern> Holes { get; } = new();

        public bool Skipped { get; set; }

        public bool OutlineClosed { get; set; }

        public double WidthMm { get; set; }

        public double HeightMm { get; set; }

        public double SmallWidthMm { get; set; }

        public double LargeWidthMm { get; set; }

        public double AreaMm2 { get; set; }

        public string Message { get; set; } = string.Empty;

        public bool Success => Item is not null;
    }

    public sealed class GaProfileBuilder
    {
        private const int MaxEntities = 5000;
        private const double FullCircleDegrees = 359.99;
        private const double MinToleranceMm = 0.05;
        private const double ToleranceFraction = 1e-4;
        private const double RadialSpreadMm = 0.5;
        private const double RadialSpreadFraction = 0.002;
        private const double ArcStepDegrees = 5.0;

        private static readonly string[] ExcludedLayerMarkers =
        {
            "dim", "text", "note", "center", "centre", "axis", "defpoints", "title", "border", "hatch", "leader"
        };

        private static readonly string[] ExcludedLineTypes =
        {
            "center", "centre", "hidden", "phantom", "dash"
        };

        private sealed class Seg
        {
            public ProfileEntity E { get; init; } = null!;

            public double Sx { get; set; }

            public double Sy { get; set; }

            public double Ex { get; set; }

            public double Ey { get; set; }

            public bool Rev { get; set; }

            public bool Used { get; set; }

            public bool IsArc => E.Kind == ProfileEntityKind.Arc;

            public void Flip()
            {
                (Sx, Sy, Ex, Ey) = (Ex, Ey, Sx, Sy);
                Rev = !Rev;
            }
        }

        private sealed class Loop
        {
            public List<Seg> Segs { get; } = new();

            public double MinX { get; set; }

            public double MinY { get; set; }

            public double MaxX { get; set; }

            public double MaxY { get; set; }

            public double Area { get; set; }

            public double BoxArea => Math.Max(MaxX - MinX, 0.0) * Math.Max(MaxY - MinY, 0.0);
        }

        public IReadOnlyList<GaProfileResult> Build(
            IReadOnlyList<GaPartClassification> parts,
            double mmPerUnit,
            GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(parts);
            ArgumentNullException.ThrowIfNull(report);

            var list = new List<GaProfileResult>();

            foreach (var part in parts)
            {
                list.Add(BuildOne(part, mmPerUnit, report));
            }

            return list;
        }

        public GaProfileResult BuildOne(GaPartClassification classification, double mmPerUnit, GenerationReport report)
        {
            ArgumentNullException.ThrowIfNull(classification);
            ArgumentNullException.ThrowIfNull(report);

            var result = new GaProfileResult { Classification = classification };
            var part = classification.Part;

            if (!classification.NeedsGeometry)
            {
                result.Skipped = true;
                result.Message = "No profile needed for " + classification.Family.ToString() + ".";
                return result;
            }

            if (classification.View is null)
            {
                result.Message = "No view was selected for this part.";
                report.Error("PROFILE_NO_VIEW", part.Name, result.Message);
                return result;
            }

            if (classification.View.EdgeOnOnly && classification.Family != GaPartFamily.Developed)
            {
                result.Message = "Only an edge-on view ("
                    + classification.View.WidthMm.ToString("0.#", CultureInfo.InvariantCulture) + " x "
                    + classification.View.HeightMm.ToString("0.#", CultureInfo.InvariantCulture)
                    + " mm) exists for this part; the true face shape is not in the drawing.";
                report.Error("PROFILE_EDGE_ON", part.Name, result.Message);
                return result;
            }
            var node = classification.View.Node;
            var mm = mmPerUnit > 0.0 ? mmPerUnit : 1.0;

            var raw = Gather(node, mm, out var removed);
            if (raw.Count == 0)
            {
                result.Message = "The selected view has no usable outline entities.";
                report.Error("PROFILE_NO_GEOMETRY", part.Name, result.Message);
                return result;
            }

            if (raw.Count > MaxEntities)
            {
                result.Message = raw.Count.ToString(CultureInfo.InvariantCulture) + " entities in the selected view; too many for one part.";
                report.Error("PROFILE_TOO_MANY", part.Name, result.Message);
                return result;
            }

            if (removed > 0)
            {
                report.Info(
                    "PROFILE_CLEANED",
                    part.Name,
                    removed.ToString(CultureInfo.InvariantCulture) + " entities removed (dimensions, centre lines, other layers, other parts).");
            }

            var extent = Math.Max(classification.View.WidthMm, classification.View.HeightMm);
            var tol = Math.Max(MinToleranceMm, extent * ToleranceFraction);

            var circles = raw.Where(e => e.Kind == ProfileEntityKind.Circle).ToList();
            var segs = raw.Where(e => e.Kind != ProfileEntityKind.Circle).Select(MakeSeg).ToList();

            var allLoops = BuildLoops(segs, tol, out var openChains);
            var loops = new List<Loop>();

            foreach (var loop in allLoops)
            {
                if (TryLoopAsCircle(loop, tol, out var circle))
                {
                    circles.Add(circle);
                }
                else
                {
                    loops.Add(loop);
                }
            }

            if (openChains.Count > 0)
            {
                report.Warn(
                    "PROFILE_OPEN_CHAINS",
                    part.Name,
                    openChains.Count.ToString(CultureInfo.InvariantCulture) + " open line chain(s) left out of the outline.");
            }

            var outerLoop = loops.OrderByDescending(l => l.BoxArea).FirstOrDefault();
            var outerCircle = circles.OrderByDescending(c => c.Radius).FirstOrDefault();

            var loopBox = outerLoop?.BoxArea ?? 0.0;
            var circleBox = outerCircle is null ? 0.0 : 4.0 * outerCircle.Radius * outerCircle.Radius;

            if (outerLoop is null && outerCircle is null)
            {
                return BuildFromOpenGeometry(result, classification, segs, circles, report);
            }

            var outerIsCircle = outerCircle is not null && circleBox >= loopBox * 0.95;

            if (outerIsCircle)
            {
                BuildRound(result, classification, outerCircle!, circles, loops, report);
            }
            else
            {
                BuildFromLoop(result, classification, outerLoop!, circles, loops, report);
            }

            return result;
        }

        private static void BuildRound(
            GaProfileResult result,
            GaPartClassification classification,
            ProfileEntity outer,
            List<ProfileEntity> circles,
            List<Loop> loops,
            GenerationReport report)
        {
            var part = classification.Part;
            var concentricTol = Math.Max(0.5, outer.Radius * 0.002);

            var concentric = circles
                .Where(c => !ReferenceEquals(c, outer)
                    && Dist(c.Cx, c.Cy, outer.Cx, outer.Cy) <= concentricTol
                    && c.Radius < outer.Radius - concentricTol)
                .OrderBy(c => c.Radius)
                .ToList();

            var holes = circles
                .Where(c => !ReferenceEquals(c, outer)
                    && !concentric.Contains(c)
                    && Dist(c.Cx, c.Cy, outer.Cx, outer.Cy) + c.Radius <= outer.Radius + concentricTol)
                .ToList();

            var cutouts = loops
                .Where(l => l.MinX >= outer.Cx - outer.Radius - concentricTol
                    && l.MaxX <= outer.Cx + outer.Radius + concentricTol
                    && l.MinY >= outer.Cy - outer.Radius - concentricTol
                    && l.MaxY <= outer.Cy + outer.Radius + concentricTol)
                .ToList();

            var bore = concentric.Count > 0 ? concentric[0] : null;
            var groups = GroupHoles(holes, outer.Cx, outer.Cy, true);

            result.Holes.AddRange(groups);
            result.OutlineClosed = true;
            result.WidthMm = Math.Round(outer.Radius * 2.0, 2);
            result.HeightMm = result.WidthMm;
            result.SmallWidthMm = bore is null ? 0.0 : Math.Round(bore.Radius * 2.0, 2);
            result.LargeWidthMm = result.WidthMm;

            var area = Math.PI * outer.Radius * outer.Radius;
            if (bore is not null)
            {
                area -= Math.PI * bore.Radius * bore.Radius;
            }

            foreach (var hole in holes)
            {
                area -= Math.PI * hole.Radius * hole.Radius;
            }

            foreach (var cutout in cutouts)
            {
                area -= cutout.Area;
            }

            result.AreaMm2 = Math.Round(Math.Max(area, 0.0), 1);

            var item = NewItem(classification);
            item.OuterDiameterMm = Math.Round(outer.Radius * 2.0, 2);
            item.InnerDiameterMm = bore is null ? null : Math.Round(bore.Radius * 2.0, 2);
            item.LengthMm = item.OuterDiameterMm;
            item.WidthMm = item.OuterDiameterMm;

            var main = groups.OrderByDescending(g => g.Count).FirstOrDefault();
            if (main is not null)
            {
                item.HoleCount = main.Count;
                item.HoleDiameterMm = main.DiameterMm;
                item.HolePcdMm = main.PcdMm;
            }

            if (concentric.Count > 1)
            {
                report.Info(
                    "PROFILE_CONCENTRIC",
                    part.Name,
                    concentric.Count.ToString(CultureInfo.InvariantCulture) + " concentric circles inside the outer circle; smallest used as bore.");
            }

            var parametric = classification.Family == GaPartFamily.Ring
                && (classification.BomKind == BomPartKind.Flange || classification.BomKind == BomPartKind.Hub)
                && groups.Count <= 1
                && cutouts.Count == 0;

            if (classification.Family == GaPartFamily.Developed && classification.Type != GaPartType.Blade)
            {
                item.LargeDiameterMm = item.OuterDiameterMm;
                item.SmallDiameterMm = item.InnerDiameterMm;
                result.Item = item;
                report.Info("PROFILE_MEASURED", part.Name, "Round view measured for flat pattern: OD " + Fmt(item.OuterDiameterMm) + " mm.");
                return;
            }

            if (parametric)
            {
                result.Item = item;
                result.Entities.Add(outer);

                if (bore is not null)
                {
                    result.Entities.Add(bore);
                }

                result.Entities.AddRange(holes);

                report.Info(
                    "PROFILE_RING",
                    part.Name,
                    "OD " + Fmt(item.OuterDiameterMm) + (item.InnerDiameterMm.HasValue ? ", ID " + Fmt(item.InnerDiameterMm) : string.Empty)
                    + (main is null ? string.Empty : ", " + main.Count.ToString(CultureInfo.InvariantCulture) + " holes Ø" + Fmt(main.DiameterMm)
                        + (main.PcdMm.HasValue ? " on PCD " + Fmt(main.PcdMm) : string.Empty)) + ".");
                return;
            }

            item.Kind = BomPartKind.ProfilePlate;
            result.Entities.Add(outer);

            if (bore is not null)
            {
                result.Entities.Add(bore);
            }

            result.Entities.AddRange(holes);

            foreach (var cutout in cutouts)
            {
                result.Entities.AddRange(cutout.Segs.Select(s => s.E));
            }

            item.Profile = result.Entities.ToList();
            result.Item = item;

            report.Info(
                "PROFILE_ROUND_PROFILE",
                part.Name,
                "Round part kept as real profile: " + result.Entities.Count.ToString(CultureInfo.InvariantCulture)
                + " entities, " + groups.Count.ToString(CultureInfo.InvariantCulture) + " hole pattern(s), "
                + cutouts.Count.ToString(CultureInfo.InvariantCulture) + " cut-out(s).");
        }

        private static void BuildFromLoop(
            GaProfileResult result,
            GaPartClassification classification,
            Loop outer,
            List<ProfileEntity> circles,
            List<Loop> loops,
            GenerationReport report)
        {
            var part = classification.Part;
            var inside = Math.Max(outer.MaxX - outer.MinX, outer.MaxY - outer.MinY) * 0.002 + MinToleranceMm;

            var holes = circles
                .Where(c => c.Cx - c.Radius >= outer.MinX - inside
                    && c.Cx + c.Radius <= outer.MaxX + inside
                    && c.Cy - c.Radius >= outer.MinY - inside
                    && c.Cy + c.Radius <= outer.MaxY + inside)
                .ToList();

            var inner = loops
                .Where(l => !ReferenceEquals(l, outer)
                    && l.MinX >= outer.MinX - inside
                    && l.MaxX <= outer.MaxX + inside
                    && l.MinY >= outer.MinY - inside
                    && l.MaxY <= outer.MaxY + inside)
                .ToList();

            var groups = GroupHoles(holes, 0.0, 0.0, false);
            result.Holes.AddRange(groups);
            result.OutlineClosed = true;

            var rectangle = IsRectangle(outer, out var length, out var width);

            result.WidthMm = Math.Round(rectangle ? length : outer.MaxX - outer.MinX, 2);
            result.HeightMm = Math.Round(rectangle ? width : outer.MaxY - outer.MinY, 2);

            var area = outer.Area;
            foreach (var h in holes)
            {
                area -= Math.PI * h.Radius * h.Radius;
            }

            foreach (var l in inner)
            {
                area -= l.Area;
            }

            result.AreaMm2 = Math.Round(Math.Max(area, 0.0), 1);

            var item = NewItem(classification);
            item.LengthMm = result.WidthMm;
            item.WidthMm = result.HeightMm;

            var main = groups.OrderByDescending(g => g.Count).FirstOrDefault();
            if (main is not null)
            {
                item.HoleCount = main.Count;
                item.HoleDiameterMm = main.DiameterMm;
            }

            if (classification.Family == GaPartFamily.Developed && classification.Type != GaPartType.Blade)
            {
                MeasureDeveloped(result, item, outer);
                result.Item = item;

                report.Info(
                    "PROFILE_MEASURED",
                    part.Name,
                    "Outline measured for flat pattern: small " + Fmt(result.SmallWidthMm) + ", large " + Fmt(result.LargeWidthMm)
                    + ", height " + Fmt(result.HeightMm) + " mm.");
                return;
            }

            var plainRectangle = rectangle
                && holes.Count == 0
                && inner.Count == 0
                && classification.Family == GaPartFamily.Rectangle;

            if (plainRectangle)
            {
                item.Kind = BomPartKind.RectangularPlate;
                result.Item = item;
                result.Entities.AddRange(outer.Segs.Select(s => s.E));

                report.Info("PROFILE_RECTANGLE", part.Name, Fmt(result.WidthMm) + " x " + Fmt(result.HeightMm) + " mm plain rectangle.");
                return;
            }

            item.Kind = BomPartKind.ProfilePlate;
            result.Entities.AddRange(outer.Segs.Select(s => s.E));

            foreach (var l in inner)
            {
                result.Entities.AddRange(l.Segs.Select(s => s.E));
            }

            result.Entities.AddRange(holes);
            item.Profile = result.Entities.ToList();
            result.Item = item;

            report.Info(
                "PROFILE_OUTLINE",
                part.Name,
                "Real outline kept: " + result.Entities.Count.ToString(CultureInfo.InvariantCulture) + " entities, "
                + Fmt(result.WidthMm) + " x " + Fmt(result.HeightMm) + " mm, "
                + groups.Sum(g => g.Count).ToString(CultureInfo.InvariantCulture) + " hole(s) in "
                + groups.Count.ToString(CultureInfo.InvariantCulture) + " size group(s), "
                + inner.Count.ToString(CultureInfo.InvariantCulture) + " cut-out(s).");
        }

        private static GaProfileResult BuildFromOpenGeometry(
            GaProfileResult result,
            GaPartClassification classification,
            List<Seg> segs,
            List<ProfileEntity> circles,
            GenerationReport report)
        {
            var part = classification.Part;

            if (segs.Count == 0 || classification.Family != GaPartFamily.Rectangle)
            {
                result.Message = "No closed outline could be built from the selected view.";
                report.Error("PROFILE_OPEN", part.Name, result.Message);
                return result;
            }

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            foreach (var s in segs)
            {
                var (ax, ay, bx, by) = SegBounds(s);
                minX = Math.Min(minX, ax);
                minY = Math.Min(minY, ay);
                maxX = Math.Max(maxX, bx);
                maxY = Math.Max(maxY, by);
            }

            var w = maxX - minX;
            var h = maxY - minY;

            if (w <= 0.0 || h <= 0.0)
            {
                result.Message = "The selected view has no usable extent.";
                report.Error("PROFILE_OPEN", part.Name, result.Message);
                return result;
            }

            var item = NewItem(classification);
            item.Kind = circles.Count == 0 ? BomPartKind.RectangularPlate : BomPartKind.ProfilePlate;
            item.LengthMm = Math.Round(Math.Max(w, h), 2);
            item.WidthMm = Math.Round(Math.Min(w, h), 2);

            result.WidthMm = item.LengthMm.Value;
            result.HeightMm = item.WidthMm.Value;
            result.AreaMm2 = Math.Round(w * h, 1);
            result.OutlineClosed = false;

            result.Entities.Add(new ProfileEntity(ProfileEntityKind.Line, minX, minY, maxX, minY, 0, 0, 0, 0, 0));
            result.Entities.Add(new ProfileEntity(ProfileEntityKind.Line, maxX, minY, maxX, maxY, 0, 0, 0, 0, 0));
            result.Entities.Add(new ProfileEntity(ProfileEntityKind.Line, maxX, maxY, minX, maxY, 0, 0, 0, 0, 0));
            result.Entities.Add(new ProfileEntity(ProfileEntityKind.Line, minX, maxY, minX, minY, 0, 0, 0, 0, 0));
            result.Entities.AddRange(circles);

            result.Holes.AddRange(GroupHoles(circles, 0.0, 0.0, false));

            if (item.Kind == BomPartKind.ProfilePlate)
            {
                item.Profile = result.Entities.ToList();
            }

            result.Item = item;

            report.Warn(
                "PROFILE_BOUNDING_BOX",
                part.Name,
                "The outline was open. A bounding rectangle " + Fmt(item.LengthMm) + " x " + Fmt(item.WidthMm) + " mm was used. Please verify.");

            return result;
        }

        private static PartBomItem NewItem(GaPartClassification classification)
        {
            var part = classification.Part;

            return new PartBomItem
            {
                Name = part.Name.ToUpperInvariant(),
                Kind = classification.BomKind,
                Material = part.Material,
                ThicknessMm = part.ThicknessMm,
                Qty = part.Qty,
                Remarks = "Built from GA view " + (classification.View?.Node.Id ?? "-") + "; type " + classification.Type.ToString() + "."
            };
        }

        private static void MeasureDeveloped(GaProfileResult result, PartBomItem item, Loop outer)
        {
            var points = new List<(double X, double Y)>();

            foreach (var s in outer.Segs)
            {
                points.Add((s.Sx, s.Sy));
                points.Add((s.Ex, s.Ey));
            }

            var vertical = EndWidths(points, false, out var vSmall, out var vLarge, out var vHeight);
            var horizontal = EndWidths(points, true, out var hSmall, out var hLarge, out var hHeight);

            var useHorizontal = horizontal > vertical;

            result.SmallWidthMm = Math.Round(useHorizontal ? hSmall : vSmall, 2);
            result.LargeWidthMm = Math.Round(useHorizontal ? hLarge : vLarge, 2);
            result.HeightMm = Math.Round(useHorizontal ? hHeight : vHeight, 2);
            result.WidthMm = Math.Round(Math.Max(result.LargeWidthMm, outer.MaxX - outer.MinX), 2);

            item.SmallDiameterMm = result.SmallWidthMm;
            item.LargeDiameterMm = result.LargeWidthMm;
            item.HeightMm = result.HeightMm;
        }

        private static double EndWidths(
            List<(double X, double Y)> points,
            bool swap,
            out double small,
            out double large,
            out double height)
        {
            var pts = swap ? points.Select(p => (X: p.Y, Y: p.X)).ToList() : points;

            var minY = pts.Min(p => p.Y);
            var maxY = pts.Max(p => p.Y);
            height = maxY - minY;

            var band = height * 0.02 + MinToleranceMm;

            var top = pts.Where(p => p.Y >= maxY - band).ToList();
            var bottom = pts.Where(p => p.Y <= minY + band).ToList();

            var topWidth = top.Count == 0 ? 0.0 : top.Max(p => p.X) - top.Min(p => p.X);
            var bottomWidth = bottom.Count == 0 ? 0.0 : bottom.Max(p => p.X) - bottom.Min(p => p.X);

            small = Math.Min(topWidth, bottomWidth);
            large = Math.Max(topWidth, bottomWidth);

            return large - small;
        }

        private static List<GaHolePattern> GroupHoles(List<ProfileEntity> holes, double cx, double cy, bool measureRadial)
        {
            var list = new List<GaHolePattern>();

            foreach (var group in holes.GroupBy(h => Math.Round(h.Radius * 2.0, 1)).OrderByDescending(g => g.Count()))
            {
                double? pcd = null;
                var members = group.ToList();

                if (measureRadial && members.Count >= 2)
                {
                    var distances = members.Select(m => Dist(m.Cx, m.Cy, cx, cy)).ToList();
                    var mean = distances.Average();
                    var spread = distances.Max(d => Math.Abs(d - mean));

                    if (spread <= Math.Max(RadialSpreadMm, mean * RadialSpreadFraction))
                    {
                        pcd = Math.Round(mean * 2.0, 2);
                    }
                }

                list.Add(new GaHolePattern
                {
                    Count = members.Count,
                    DiameterMm = group.Key,
                    PcdMm = pcd
                });
            }

            return list;
        }

        private static bool IsRectangle(Loop loop, out double length, out double width)
        {
            length = 0.0;
            width = 0.0;

            if (loop.Segs.Count != 4 || loop.Segs.Any(s => s.IsArc))
            {
                return false;
            }

            var vectors = loop.Segs.Select(s => (X: s.Ex - s.Sx, Y: s.Ey - s.Sy)).ToList();

            for (var i = 0; i < 4; i++)
            {
                var a = vectors[i];
                var b = vectors[(i + 1) % 4];
                var la = Math.Sqrt(a.X * a.X + a.Y * a.Y);
                var lb = Math.Sqrt(b.X * b.X + b.Y * b.Y);

                if (la <= 1e-9 || lb <= 1e-9)
                {
                    return false;
                }

                var dot = a.X * b.X + a.Y * b.Y;
                if (Math.Abs(dot) > 0.01 * la * lb)
                {
                    return false;
                }
            }

            var l0 = Math.Sqrt(vectors[0].X * vectors[0].X + vectors[0].Y * vectors[0].Y);
            var l1 = Math.Sqrt(vectors[1].X * vectors[1].X + vectors[1].Y * vectors[1].Y);

            length = Math.Max(l0, l1);
            width = Math.Min(l0, l1);
            return true;
        }

        private static List<ProfileEntity> Gather(AssemblyNode node, double mm, out int removed)
        {
            var g = node.Geometry;
            var kept = new List<(ProfileEntity E, string? Layer)>();
            removed = 0;

            var b = node.Bounds;
            var margin = Math.Max(b.Width, b.Height) * 0.01 + 1e-6;
            var box = b.Expand(margin);

            foreach (var l in g.Lines)
            {
                if (SkipLayer(l.Layer) || SkipLineType(l.LineType))
                {
                    removed++;
                    continue;
                }

                if (!Inside(box, l.Start.X, l.Start.Y) || !Inside(box, l.End.X, l.End.Y))
                {
                    removed++;
                    continue;
                }

                if (l.Length * mm <= 1e-6)
                {
                    continue;
                }

                kept.Add((new ProfileEntity(ProfileEntityKind.Line, l.Start.X * mm, l.Start.Y * mm, l.End.X * mm, l.End.Y * mm, 0, 0, 0, 0, 0), l.Layer));
            }

            foreach (var a in g.Arcs)
            {
                if (SkipLayer(a.Layer) || SkipLineType(a.LineType))
                {
                    removed++;
                    continue;
                }

                if (!Inside(box, a.Center.X, a.Center.Y) || a.Radius * mm <= 1e-6)
                {
                    removed++;
                    continue;
                }

                var sweep = (a.EndAngle - a.StartAngle) % 360.0;
                if (sweep <= 1e-9)
                {
                    sweep += 360.0;
                }

                if (sweep >= FullCircleDegrees)
                {
                    kept.Add((new ProfileEntity(ProfileEntityKind.Circle, 0, 0, 0, 0, a.Center.X * mm, a.Center.Y * mm, a.Radius * mm, 0, 360), a.Layer));
                    continue;
                }

                kept.Add((new ProfileEntity(ProfileEntityKind.Arc, 0, 0, 0, 0, a.Center.X * mm, a.Center.Y * mm, a.Radius * mm, a.StartAngle, a.EndAngle), a.Layer));
            }

            foreach (var c in g.Circles)
            {
                if (SkipLayer(c.Layer) || SkipLineType(c.LineType))
                {
                    removed++;
                    continue;
                }

                if (!Inside(box, c.Center.X, c.Center.Y) || c.Radius * mm <= 1e-6)
                {
                    removed++;
                    continue;
                }

                kept.Add((new ProfileEntity(ProfileEntityKind.Circle, 0, 0, 0, 0, c.Center.X * mm, c.Center.Y * mm, c.Radius * mm, 0, 360), c.Layer));
            }

            foreach (var p in g.Polylines)
            {
                if (SkipLayer(p.Layer) || SkipLineType(p.LineType))
                {
                    removed++;
                    continue;
                }

                if (p.Vertices.Count < 2 || p.Vertices.Any(v => !Inside(box, v.X, v.Y)))
                {
                    removed++;
                    continue;
                }

                var count = p.Vertices.Count;
                var segments = p.IsClosed ? count : count - 1;
                var pieces = new List<ProfileEntity>();

                for (var i = 0; i < segments; i++)
                {
                    var v0 = p.Vertices[i];
                    var v1 = p.Vertices[(i + 1) % count];
                    AddBulgeSegment(pieces, v0.X * mm, v0.Y * mm, v1.X * mm, v1.Y * mm, v0.Bulge);
                }

                foreach (var piece in pieces)
                {
                    kept.Add((piece, p.Layer));
                }
            }

            var layer = node.Layer;

            if (!string.IsNullOrWhiteSpace(layer))
            {
                var same = kept.Count(k => string.Equals(k.Layer, layer, StringComparison.OrdinalIgnoreCase));

                if (same >= 3 && same < kept.Count)
                {
                    removed += kept.Count - same;
                    kept = kept.Where(k => string.Equals(k.Layer, layer, StringComparison.OrdinalIgnoreCase)).ToList();
                }
            }

            return kept.Select(k => k.E).ToList();
        }

        private static bool SkipLayer(string? layer)
        {
            return !string.IsNullOrWhiteSpace(layer)
                && ExcludedLayerMarkers.Any(m => layer.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SkipLineType(string? lineType)
        {
            return !string.IsNullOrWhiteSpace(lineType)
                && ExcludedLineTypes.Any(m => lineType.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Inside(BoundingBox2D box, double x, double y)
        {
            return x >= box.MinX && x <= box.MaxX && y >= box.MinY && y <= box.MaxY;
        }

        private static void AddBulgeSegment(List<ProfileEntity> list, double x0, double y0, double x1, double y1, double bulge)
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

        private static Seg MakeSeg(ProfileEntity e)
        {
            if (e.Kind == ProfileEntityKind.Arc)
            {
                var a0 = e.StartAngle * Math.PI / 180.0;
                var a1 = e.EndAngle * Math.PI / 180.0;

                return new Seg
                {
                    E = e,
                    Sx = e.Cx + e.Radius * Math.Cos(a0),
                    Sy = e.Cy + e.Radius * Math.Sin(a0),
                    Ex = e.Cx + e.Radius * Math.Cos(a1),
                    Ey = e.Cy + e.Radius * Math.Sin(a1)
                };
            }

            return new Seg { E = e, Sx = e.X1, Sy = e.Y1, Ex = e.X2, Ey = e.Y2 };
        }

        private static List<Loop> BuildLoops(List<Seg> segs, double tol, out List<List<Seg>> openChains)
        {
            var loops = new List<Loop>();
            openChains = new List<List<Seg>>();

            foreach (var seed in segs)
            {
                if (seed.Used)
                {
                    continue;
                }

                seed.Used = true;

                var chain = new List<Seg> { seed };
                var closed = false;

                while (!closed)
                {
                    var tail = chain[^1];
                    var next = FindNext(segs, tail.Ex, tail.Ey, tol);

                    if (next is null)
                    {
                        break;
                    }

                    next.Used = true;

                    if (Dist(next.Sx, next.Sy, tail.Ex, tail.Ey) > tol)
                    {
                        next.Flip();
                    }

                    chain.Add(next);

                    if (Dist(next.Ex, next.Ey, chain[0].Sx, chain[0].Sy) <= tol)
                    {
                        closed = true;
                    }
                }

                while (!closed)
                {
                    var head = chain[0];
                    var prev = FindNext(segs, head.Sx, head.Sy, tol);

                    if (prev is null)
                    {
                        break;
                    }

                    prev.Used = true;

                    if (Dist(prev.Ex, prev.Ey, head.Sx, head.Sy) > tol)
                    {
                        prev.Flip();
                    }

                    chain.Insert(0, prev);

                    if (Dist(prev.Sx, prev.Sy, chain[^1].Ex, chain[^1].Ey) <= tol)
                    {
                        closed = true;
                    }
                }

                if (!closed || chain.Count < 2)
                {
                    openChains.Add(chain);
                    continue;
                }

                var loop = new Loop();
                loop.Segs.AddRange(chain);
                Measure(loop);
                loops.Add(loop);
            }

            return loops;
        }

        private static Seg? FindNext(List<Seg> segs, double x, double y, double tol)
        {
            Seg? best = null;
            var bestDistance = double.MaxValue;

            foreach (var s in segs)
            {
                if (s.Used)
                {
                    continue;
                }

                var d = Math.Min(Dist(s.Sx, s.Sy, x, y), Dist(s.Ex, s.Ey, x, y));

                if (d <= tol && d < bestDistance)
                {
                    best = s;
                    bestDistance = d;
                }
            }

            return best;
        }

        private static bool TryLoopAsCircle(Loop loop, double tol, out ProfileEntity circle)
        {
            circle = null!;

            if (loop.Segs.Count == 0 || loop.Segs.Any(s => !s.IsArc))
            {
                return false;
            }

            var first = loop.Segs[0].E;
            var limit = tol * 10.0;

            foreach (var s in loop.Segs)
            {
                if (Dist(s.E.Cx, s.E.Cy, first.Cx, first.Cy) > limit || Math.Abs(s.E.Radius - first.Radius) > limit)
                {
                    return false;
                }
            }

            circle = new ProfileEntity(ProfileEntityKind.Circle, 0, 0, 0, 0, first.Cx, first.Cy, first.Radius, 0, 360);
            return true;
        }

        private static void Measure(Loop loop)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;

            foreach (var s in loop.Segs)
            {
                var (ax, ay, bx, by) = SegBounds(s);
                minX = Math.Min(minX, ax);
                minY = Math.Min(minY, ay);
                maxX = Math.Max(maxX, bx);
                maxY = Math.Max(maxY, by);
            }

            loop.MinX = minX;
            loop.MinY = minY;
            loop.MaxX = maxX;
            loop.MaxY = maxY;

            var points = new List<(double X, double Y)>();

            foreach (var s in loop.Segs)
            {
                SamplePoints(s, points);
            }

            var sum = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var q = points[(i + 1) % points.Count];
                sum += p.X * q.Y - q.X * p.Y;
            }

            loop.Area = Math.Abs(sum) / 2.0;
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) SegBounds(Seg s)
        {
            var minX = Math.Min(s.Sx, s.Ex);
            var minY = Math.Min(s.Sy, s.Ey);
            var maxX = Math.Max(s.Sx, s.Ex);
            var maxY = Math.Max(s.Sy, s.Ey);

            if (!s.IsArc)
            {
                return (minX, minY, maxX, maxY);
            }

            var e = s.E;
            var sweep = Sweep(e);

            for (var k = 0; k < 4; k++)
            {
                var angle = k * 90.0;
                var delta = ((angle - e.StartAngle) % 360.0 + 360.0) % 360.0;

                if (delta <= sweep)
                {
                    var x = e.Cx + e.Radius * Math.Cos(angle * Math.PI / 180.0);
                    var y = e.Cy + e.Radius * Math.Sin(angle * Math.PI / 180.0);
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            return (minX, minY, maxX, maxY);
        }

        private static void SamplePoints(Seg s, List<(double X, double Y)> points)
        {
            if (!s.IsArc)
            {
                points.Add((s.Sx, s.Sy));
                return;
            }

            var e = s.E;
            var sweep = Sweep(e);
            var steps = Math.Max(2, (int)Math.Ceiling(sweep / ArcStepDegrees));

            for (var i = 0; i < steps; i++)
            {
                var t = (double)i / steps;
                var angle = s.Rev ? e.StartAngle + sweep - t * sweep : e.StartAngle + t * sweep;
                var rad = angle * Math.PI / 180.0;
                points.Add((e.Cx + e.Radius * Math.Cos(rad), e.Cy + e.Radius * Math.Sin(rad)));
            }
        }

        private static double Sweep(ProfileEntity e)
        {
            var sweep = (e.EndAngle - e.StartAngle) % 360.0;

            if (sweep <= 1e-9)
            {
                sweep += 360.0;
            }

            return sweep;
        }

        private static double Dist(double x0, double y0, double x1, double y1)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Fmt(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";
        }
    }
}
