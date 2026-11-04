using CadToCode.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using netDxf;
using netDxf.Entities;
using System;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CadToCode.Database.Entities;
using CadToCode.Models.Options;
namespace Web.Controllers;

public class DxfProcessingController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _cfg;

    public DxfProcessingController(ApplicationDbContext db, IWebHostEnvironment env, IConfiguration cfg)
    {
        _db = db;
        _env = env;
        _cfg = cfg;
    }

    private ComponentSettings Settings() =>
        _cfg.GetSection("ComponentSettings").Get<ComponentSettings>() ?? new ComponentSettings();

    [HttpGet]
    public IActionResult Index() => View();

    // ---------------------------------------------------------------- upload

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> UploadGaDxf(IFormFile? file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "No file uploaded." });
        if (!Path.GetExtension(file.FileName).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { success = false, message = "Only .dxf files are allowed." });

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);
        var storedName = $"{Guid.NewGuid():N}.dxf";
        var fullPath = Path.Combine(uploadsDir, storedName);

        await using (var fs = new FileStream(fullPath, FileMode.Create))
            await file.CopyToAsync(fs);

        var settings = Settings();
        Analysis analysis;
        try
        {
            var doc = DxfDocument.Load(fullPath) ?? throw new Exception("DXF file could not be parsed.");
            analysis = Analyze(doc, settings);
        }
        catch (Exception ex)
        {
            System.IO.File.Delete(fullPath);
            return BadRequest(new { success = false, message = "Could not read DXF: " + ex.Message });
        }

        var job = new DxfJob
        {
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFilePath = "uploads/" + storedName,
            Status = "Parsed"
        };
        foreach (var c in analysis.Components)
            job.Parts.Add(new ExtractedPart { LayerName = c.Key, EntityCount = c.Value.Count });

        _db.DxfJobs.Add(job);
        await _db.SaveChangesAsync();

        var rules = await _db.PartRules.ToDictionaryAsync(r => r.RuleKey);
        var items = new List<ReviewItem>();
        foreach (var c in analysis.Components)
        {
            var key = "C:" + c.Key;
            var item = new ReviewItem { Key = key, Kind = "component", Name = c.Key, Entities = c.Value.Count };
            ApplyRule(item, rules);
            items.Add(item);
        }
        foreach (var (c, key) in CalloutKeys(analysis, settings))
        {
            var item = new ReviewItem { Key = key, Kind = "callout", Name = c.Name, DrgNo = c.DrgNo };
            ApplyRule(item, rules);
            items.Add(item);
        }

        return Json(new { success = true, jobId = job.Id, layerCount = items.Count, items });
    }

    private sealed class ReviewItem
    {
        public string Key { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public int Entities { get; set; }
        public string DrgNo { get; set; } = "";
        public bool Include { get; set; } = true;
        public string? MergeInto { get; set; }
    }

    private sealed class Selection
    {
        public string Key { get; set; } = "";
        public string? Name { get; set; }
        public bool Include { get; set; } = true;
        public string? MergeInto { get; set; }
    }

    private static void ApplyRule(ReviewItem item, Dictionary<string, PartRule> rules)
    {
        if (!rules.TryGetValue(item.Key, out var r)) return;
        if (!string.IsNullOrWhiteSpace(r.DisplayName)) item.Name = r.DisplayName!;
        item.Include = r.Include;
        item.MergeInto = r.MergeInto;
    }

    private static List<(Callout C, string Key)> CalloutKeys(Analysis a, ComponentSettings s)
    {
        var res = new List<(Callout, string)>();
        var seen = new Dictionary<string, int>();
        foreach (var c in a.Callouts)
        {
            var rule = RuleFor(c.Name, s);
            if (rule?.Component != null && a.Components.ContainsKey(rule.Component)) continue;
            var key = "K:" + c.Name + "|" + c.DrgNo;
            seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 1;
            if (seen[key] > 1) key += "#" + seen[key];
            res.Add((c, key));
        }
        return res;
    }

    // -------------------------------------------------------------- generate

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateManufacturingDrawings(int jobId, string? selections)
    {
        var job = await _db.DxfJobs.Include(j => j.Parts).FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null) return NotFound(new { success = false, message = "Job not found." });

        var src = Path.Combine(_env.WebRootPath, job.StoredFilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(src))
            return NotFound(new { success = false, message = "Original DXF file is missing." });

        var doc = DxfDocument.Load(src);
        if (doc == null) return BadRequest(new { success = false, message = "Could not read original DXF." });

        var settings = Settings();
        var analysis = Analyze(doc, settings);
        var assigned = AssignAnnotations(analysis, settings);

        var sels = new Dictionary<string, Selection>();
        if (!string.IsNullOrWhiteSpace(selections))
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<Selection>>(selections,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            foreach (var x in list) sels[x.Key] = x;
        }
        Selection? Sel(string key) => sels.TryGetValue(key, out var v) ? v : null;
        string Display(string key, string fallback) =>
            string.IsNullOrWhiteSpace(Sel(key)?.Name) ? fallback : Sel(key)!.Name!.Trim();

        var relDir = $"uploads/Drawings_Job_{jobId}";
        var outDir = Path.Combine(_env.WebRootPath, relDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.dxf")) System.IO.File.Delete(old);
        _db.GeneratedDrawings.RemoveRange(_db.GeneratedDrawings.Where(g => g.JobId == jobId));

        var results = new List<object>();
        var skipped = new List<string>(analysis.SkippedSmall);
        var index = 0;

        var compDrg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in analysis.Callouts)
        {
            var rule = RuleFor(c.Name, settings);
            if (rule?.Component != null && c.DrgNo.Length > 0) compDrg[rule.Component] = c.DrgNo;
        }

        // components, with optional merging into another component
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in job.Parts.OrderBy(p => p.Id))
        {
            if (!analysis.Components.ContainsKey(part.LayerName)) { skipped.Add(part.LayerName); continue; }
            var sel = Sel("C:" + part.LayerName);
            if (sel != null && !sel.Include) continue;
            var target = part.LayerName;
            var mi = sel?.MergeInto;
            if (!string.IsNullOrEmpty(mi) && mi.StartsWith("C:"))
            {
                var t = mi.Substring(2);
                if (analysis.Components.ContainsKey(t) && !t.Equals(part.LayerName, StringComparison.OrdinalIgnoreCase)) target = t;
            }
            if (!groups.TryGetValue(target, out var members)) groups[target] = members = new List<string>();
            members.Add(part.LayerName);
        }

        foreach (var g in groups)
        {
            index++;
            var target = g.Key;
            var items = new List<EntityObject>();
            foreach (var member in g.Value)
            {
                foreach (var e in analysis.Components[member]) AddClone(items, e);
                foreach (var d in assigned.Dims[member])
                    if (d.Block != null)
                        foreach (var be in d.Block.Entities) AddClone(items, be);
                foreach (var t in assigned.Notes[member]) AddClone(items, t);
            }

            var title = Display("C:" + target, target);
            var drgNo = compDrg.TryGetValue(target, out var cd) ? cd : $"J{jobId}-{index:00}";
            var partDoc = new DxfDocument(doc.DrawingVariables.AcadVer);
            var count = BuildSheet(partDoc, items, title, drgNo, job.OriginalFileName);

            var fileName = $"{index:00}_{Sanitize(title)}.dxf";
            partDoc.Save(Path.Combine(outDir, fileName));

            _db.GeneratedDrawings.Add(new GeneratedDrawing
            {
                JobId = jobId,
                PartId = job.Parts.FirstOrDefault(p => p.LayerName.Equals(target, StringComparison.OrdinalIgnoreCase))?.Id,
                GroupName = title,
                FileName = fileName,
                FilePath = $"{relDir}/{fileName}"
            });
            results.Add(new { part = title, fileName, entities = count, url = $"/{relDir}/{fileName}" });
        }

        // callout and flange sheets
        foreach (var (c, key) in CalloutKeys(analysis, settings))
        {
            var sel = Sel(key);
            if (sel != null && !sel.Include) continue;
            var rule = RuleFor(c.Name, settings);

            var hw = rule != null && rule.HalfWidth > 0 ? rule.HalfWidth : settings.DefaultHalfSize;
            var hh = rule != null && rule.HalfHeight > 0 ? rule.HalfHeight : settings.DefaultHalfSize;
            double cx = c.X, cy = c.Y;
            if (rule != null && rule.Snap && !c.HasLeader)
                SnapToNearest(analysis, c.X, c.Y, rule.SnapMax, out cx, out cy);
            var win = new Box();
            win.Add(cx - hw, cy - hh);
            win.Add(cx + hw, cy + hh);

            var items = CropEntities(analysis, win);
            if (items.Count == 0) { skipped.Add(c.Name); continue; }
            index++;

            var title = Display(key, c.Name);
            var drgNo = c.DrgNo.Length > 0 ? c.DrgNo : $"J{jobId}-{index:00}";
            var partDoc = new DxfDocument(doc.DrawingVariables.AcadVer);
            var count = BuildSheet(partDoc, items, title, drgNo, job.OriginalFileName);

            var fileName = $"{index:00}_{Sanitize((c.DrgNo.Length > 0 ? c.DrgNo + "_" : "") + title)}.dxf";
            partDoc.Save(Path.Combine(outDir, fileName));

            _db.GeneratedDrawings.Add(new GeneratedDrawing
            {
                JobId = jobId,
                PartId = null,
                GroupName = title,
                FileName = fileName,
                FilePath = $"{relDir}/{fileName}"
            });
            results.Add(new { part = title, fileName, entities = count, url = $"/{relDir}/{fileName}" });
        }

        // remember the engineer's choices for the next upload
        foreach (var x in sels.Values)
        {
            var r = await _db.PartRules.FindAsync(x.Key);
            if (r == null) { r = new PartRule { RuleKey = x.Key }; _db.PartRules.Add(r); }
            r.DisplayName = x.Name;
            r.Include = x.Include;
            r.MergeInto = x.MergeInto;
            r.UpdatedAt = DateTime.UtcNow;
        }

        job.Status = "Generated";
        await _db.SaveChangesAsync();
        return Json(new { success = true, jobId, count = results.Count, files = results, skipped, unmatchedDimensions = assigned.Unmatched });
    }

    [HttpGet]
    public async Task<IActionResult> DownloadAllZip(int jobId)
    {
        var items = await _db.GeneratedDrawings.Where(g => g.JobId == jobId).ToListAsync();
        if (items.Count == 0) return NotFound("No drawings generated for this job.");

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var d in items)
            {
                var p = Path.Combine(_env.WebRootPath, d.FilePath.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(p)) zip.CreateEntryFromFile(p, d.FileName);
            }
        }
        ms.Position = 0;
        return File(ms, "application/zip", $"Job_{jobId}_Manufacturing_Drawings.zip");
    }

    // -------------------------------------------------------------- analysis

    private sealed class Analysis
    {
        public Dictionary<string, List<EntityObject>> Components = new(StringComparer.OrdinalIgnoreCase);
        public List<Dimension> Dimensions = new();
        public List<EntityObject> Notes = new();
        public List<string> SkippedSmall = new();
        public List<EntityObject> AllGeometry = new();
        public List<Callout> Callouts = new();
    }

    private sealed class Callout
    {
        public string Name = "";
        public string DrgNo = "";
        public double X, Y;
        public bool HasLeader;
    }

    private sealed class Assigned
    {
        public Dictionary<string, List<Dimension>> Dims = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<EntityObject>> Notes = new(StringComparer.OrdinalIgnoreCase);
        public int Unmatched;
    }

    private static bool IsSupported(EntityObject e) =>
    e is Line || e is Polyline2D || e is Polyline3D || e is Circle || e is Arc ||
    e is Ellipse || e is Spline || e is Text || e is MText || e is netDxf.Entities.Point ||
    e is Hatch || e is Solid;
    private static void Flatten(EntityObject e, List<EntityObject> output, int depth)
    {
        if (e is Insert ins)
        {
            if (depth > 10) return;
            List<EntityObject> sub;
            try { sub = ins.Explode(); } catch { return; }
            foreach (var s in sub)
            {
                if (s.Layer.Name == "0") s.Layer = ins.Layer;
                Flatten(s, output, depth + 1);
            }
        }
        else output.Add(e);
    }

    private static Analysis Analyze(DxfDocument doc, ComponentSettings s)
    {
        var a = new Analysis();
        var flat = new List<EntityObject>();
        foreach (var e in doc.Entities.All.ToList()) Flatten(e, flat, 0);

        var leaders = new List<List<(double X, double Y)>>();
        var texts = new List<EntityObject>();
        foreach (var e in flat)
        {
            if (e is Leader ld) leaders.Add(ld.Vertexes.Select(v => ((double)v.X, (double)v.Y)).ToList());
            else if (e is Text || e is MText) texts.Add(e);
        }

        var ignore = new HashSet<string>(s.IgnoreLayers, StringComparer.OrdinalIgnoreCase);
        var ignoreRx = s.IgnoreLayerPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToList();
        var dimLayers = new HashSet<string>(s.DimensionLayers, StringComparer.OrdinalIgnoreCase);
        var mergeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in s.Merge)
            foreach (var layer in m.Value) mergeMap[layer] = m.Key.ToUpperInvariant();
        var suffix = s.ViewSuffixes.Count > 0
            ? new Regex("(" + string.Join("|", s.ViewSuffixes.Select(Regex.Escape)) + ")+$", RegexOptions.IgnoreCase)
            : null;

        foreach (var e in flat)
        {
            var layer = e.Layer.Name;
            if (e is Dimension d) { if (dimLayers.Contains(layer)) a.Dimensions.Add(d); continue; }
            if ((e is Text || e is MText) && dimLayers.Contains(layer)) { a.Notes.Add(e); continue; }
            if (!IsSupported(e)) continue;
            if (ignore.Contains(layer) || ignoreRx.Any(r => r.IsMatch(layer))) continue;
            a.AllGeometry.Add(e);

            string name;
            if (mergeMap.TryGetValue(layer, out var merged)) name = merged;
            else
            {
                name = (suffix != null ? suffix.Replace(layer, "") : layer).ToUpperInvariant();
                if (name.Length == 0) name = layer.ToUpperInvariant();
            }
            if (!a.Components.TryGetValue(name, out var list)) a.Components[name] = list = new List<EntityObject>();
            list.Add(e);
        }

        foreach (var small in a.Components.Where(c => c.Value.Count < s.MinEntities).Select(c => c.Key).ToList())
        {
            a.Components.Remove(small);
            a.SkippedSmall.Add(small);
        }

        ParseCallouts(a, texts, leaders, s);
        return a;
    }

    // -------------------------------------------------------------- callouts

    private static string StripMText(string raw)
    {
        var t = raw.Replace("\\P", "\n").Replace("\\~", " ");
        t = Regex.Replace(t, @"\\[A-Za-z][^;\\{}]*;", "");
        t = Regex.Replace(t, @"\\[LlOoKk]", "");
        return t.Replace("{", "").Replace("}", "");
    }

    private static double SegDist(double px, double py, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - a.X) * dx + (py - a.Y) * dy) / len));
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static void ParseCallouts(Analysis a, List<EntityObject> texts, List<List<(double X, double Y)>> leaders, ComponentSettings s)
    {
        var calloutRx = new Regex(s.CalloutRegex, RegexOptions.IgnoreCase);
        var labelRx = new Regex(s.LabelRegex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var seen = new HashSet<string>();

        foreach (var t in texts)
        {
            var raw = t is MText mt ? mt.Value : ((Text)t).Value;
            var pos = t is MText mt2 ? mt2.Position : ((Text)t).Position;
            var clean = StripMText(raw ?? "");
            var one = Regex.Replace(clean, @"\s+", " ").Trim();
            var m = calloutRx.Match(clean);
            var isLabel = !m.Success && one.Length > 0 && one.Length <= s.MaxLabelLength && labelRx.IsMatch(one);
            if (!m.Success && !isLabel) continue;

            List<(double X, double Y)>? bestLd = null;
            var bestD = double.MaxValue;
            foreach (var pts in leaders)
            {
                if (pts.Count < 2) continue;
                var d = SegDist(pos.X, pos.Y, pts[^2], pts[^1]);
                if (d < bestD) { bestD = d; bestLd = pts; }
            }

            double tipX = pos.X, tipY = pos.Y;
            var hasLeader = false;
            if (bestLd != null && bestD <= s.LeaderTolerance)
            {
                var chain = new List<(double X, double Y)>(bestLd);
                for (var pass = 0; pass < 3; pass++)
                    foreach (var other in leaders)
                    {
                        if (ReferenceEquals(other, bestLd)) continue;
                        if (!other.Any(v => chain.Any(c => Math.Abs(c.X - v.X) <= 5 && Math.Abs(c.Y - v.Y) <= 5))) continue;
                        foreach (var v in other) if (!chain.Contains(v)) chain.Add(v);
                    }
                var a0 = bestLd[^2];
                var b0 = bestLd[^1];
                var far = chain.OrderByDescending(v => SegDist(v.X, v.Y, a0, b0)).First();
                tipX = far.X;
                tipY = far.Y;
                hasLeader = true;
            }

            var lines = clean.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !calloutRx.IsMatch(l));
            var name = string.Join(" ", lines);
            if (name.Length == 0) continue;
            var drg = m.Success ? m.Groups[1].Value.Trim().TrimEnd('.') : "";
            if (!seen.Add(name + "|" + drg + "|" + Math.Round(tipX) + "|" + Math.Round(tipY))) continue;
            a.Callouts.Add(new Callout { Name = name, DrgNo = drg, X = tipX, Y = tipY, HasLeader = hasLeader });
        }
    }

    private static CalloutRule? RuleFor(string name, ComponentSettings s) =>
        s.Callouts.FirstOrDefault(r => Regex.IsMatch(name, r.Match, RegexOptions.IgnoreCase));

    private static void SnapToNearest(Analysis a, double x, double y, double max, out double cx, out double cy)
    {
        cx = x;
        cy = y;
        var best = max;
        foreach (var e in a.AllGeometry)
        {
            if (e is Text || e is MText) continue;
            var b = BoxOf(e);
            if (!b.Valid) continue;
            var d = b.DistanceTo(x, y);
            if (d < best)
            {
                best = d;
                cx = (b.MinX + b.MaxX) / 2;
                cy = (b.MinY + b.MaxY) / 2;
            }
        }
    }

    private static bool Inside(Box b, Box w) =>
        b.Valid && b.MinX >= w.MinX && b.MaxX <= w.MaxX && b.MinY >= w.MinY && b.MaxY <= w.MaxY;

    private static bool ClipLine(Line l, Box w, out EntityObject clipped)
    {
        clipped = l;
        double x0 = l.StartPoint.X, y0 = l.StartPoint.Y, x1 = l.EndPoint.X, y1 = l.EndPoint.Y;
        double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;

        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }

        if (!Clip(-dx, x0 - w.MinX) || !Clip(dx, w.MaxX - x0) || !Clip(-dy, y0 - w.MinY) || !Clip(dy, w.MaxY - y0)) return false;
        if (t1 <= t0) return false;
        if (l.Clone() is not Line c) return false;
        c.StartPoint = new Vector3(x0 + t0 * dx, y0 + t0 * dy, l.StartPoint.Z);
        c.EndPoint = new Vector3(x0 + t1 * dx, y0 + t1 * dy, l.EndPoint.Z);
        clipped = c;
        return true;
    }

    private static List<EntityObject> CropEntities(Analysis a, Box win)
    {
        var list = new List<EntityObject>();
        var span = Math.Max(win.MaxX - win.MinX, win.MaxY - win.MinY);
        foreach (var e in a.AllGeometry)
        {
            if (e is Line l)
            {
                if (ClipLine(l, win, out var c)) list.Add(c);
                continue;
            }
            var b = BoxOf(e);
            if (!b.Valid) continue;
            if (e is Circle || e is Arc)
            {
                var cx = (b.MinX + b.MaxX) / 2;
                var cy = (b.MinY + b.MaxY) / 2;
                if (cx >= win.MinX && cx <= win.MaxX && cy >= win.MinY && cy <= win.MaxY && (b.MaxX - b.MinX) <= span)
                    AddClone(list, e);
                continue;
            }
            if (Inside(b, win)) AddClone(list, e);
        }
        foreach (var d in a.Dimensions)
            if (d.Block != null && Inside(BoxOfDimension(d), win))
                foreach (var be in d.Block.Entities) AddClone(list, be);
        foreach (var n in a.Notes)
            if (Inside(BoxOf(n), win)) AddClone(list, n);
        return list;
    }

    // ------------------------------------------------------------- geometry

    private struct Box
    {
        public double MinX, MinY, MaxX, MaxY;
        public bool Valid;

        public void Add(double x, double y)
        {
            if (!Valid) { MinX = MaxX = x; MinY = MaxY = y; Valid = true; return; }
            if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
        }

        public void Add(Box b)
        {
            if (!b.Valid) return;
            Add(b.MinX, b.MinY); Add(b.MaxX, b.MaxY);
        }

        public double DistanceTo(double x, double y)
        {
            var dx = Math.Max(Math.Max(MinX - x, 0), x - MaxX);
            var dy = Math.Max(Math.Max(MinY - y, 0), y - MaxY);
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static Box BoxOf(EntityObject e)
    {
        var b = new Box();
        switch (e)
        {
            case Line l: b.Add(l.StartPoint.X, l.StartPoint.Y); b.Add(l.EndPoint.X, l.EndPoint.Y); break;
            case Circle c: b.Add(c.Center.X - c.Radius, c.Center.Y - c.Radius); b.Add(c.Center.X + c.Radius, c.Center.Y + c.Radius); break;
            case Arc a: b.Add(a.Center.X - a.Radius, a.Center.Y - a.Radius); b.Add(a.Center.X + a.Radius, a.Center.Y + a.Radius); break;
            case Ellipse el:
                var r = Math.Max(el.MajorAxis, el.MinorAxis);
                b.Add(el.Center.X - r, el.Center.Y - r); b.Add(el.Center.X + r, el.Center.Y + r); break;
            case Polyline2D p2: foreach (var v in p2.Vertexes) b.Add(v.Position.X, v.Position.Y); break;
            case Solid so:
                b.Add(so.FirstVertex.X, so.FirstVertex.Y); b.Add(so.SecondVertex.X, so.SecondVertex.Y);
                b.Add(so.ThirdVertex.X, so.ThirdVertex.Y); b.Add(so.FourthVertex.X, so.FourthVertex.Y); break;
            case Polyline3D p3: foreach (var v in p3.Vertexes) b.Add(v.X, v.Y); break;
            case Text t: b.Add(t.Position.X, t.Position.Y); break;
            case MText m: b.Add(m.Position.X, m.Position.Y); break;
            case netDxf.Entities.Point pt: b.Add(pt.Position.X, pt.Position.Y); break;
        }
        return b;
    }

    private static Box BoxOfDimension(Dimension d)
    {
        var b = new Box();
        if (d.Block != null)
            foreach (var e in d.Block.Entities) b.Add(BoxOf(e));
        return b;
    }

    private static List<(double X, double Y)> DimPoints(Dimension d)
    {
        var pts = new List<(double X, double Y)>();
        switch (d)
        {
            case LinearDimension ld:
                pts.Add((ld.FirstReferencePoint.X, ld.FirstReferencePoint.Y));
                pts.Add((ld.SecondReferencePoint.X, ld.SecondReferencePoint.Y));
                break;
            case AlignedDimension ad:
                pts.Add((ad.FirstReferencePoint.X, ad.FirstReferencePoint.Y));
                pts.Add((ad.SecondReferencePoint.X, ad.SecondReferencePoint.Y));
                break;
            default:
                var b = BoxOfDimension(d);
                if (b.Valid) pts.Add(((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2));
                break;
        }
        return pts;
    }

    private static Assigned AssignAnnotations(Analysis a, ComponentSettings s)
    {
        var res = new Assigned();
        var boxes = new Dictionary<string, List<Box>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in a.Components)
        {
            res.Dims[c.Key] = new List<Dimension>();
            res.Notes[c.Key] = new List<EntityObject>();
            boxes[c.Key] = c.Value.Select(BoxOf).Where(b => b.Valid).ToList();
        }

        foreach (var d in a.Dimensions)
        {
            var pts = DimPoints(d);
            string? best = null;
            var bestScore = 0;
            foreach (var c in a.Components)
            {
                var score = 0;
                foreach (var p in pts)
                    if (boxes[c.Key].Any(b => b.DistanceTo(p.X, p.Y) <= s.MatchTolerance)) score++;
                if (score > bestScore || (score == bestScore && score > 0 && best != null && c.Value.Count < a.Components[best].Count))
                {
                    best = c.Key;
                    bestScore = score;
                }
            }
            if (best == null) res.Unmatched++; else res.Dims[best].Add(d);
        }

        foreach (var n in a.Notes)
        {
            var nb = BoxOf(n);
            if (!nb.Valid) continue;
            string? best = null;
            var bestDist = double.MaxValue;
            foreach (var c in a.Components)
                foreach (var b in boxes[c.Key])
                {
                    var dist = b.DistanceTo(nb.MinX, nb.MinY);
                    if (dist < bestDist) { bestDist = dist; best = c.Key; }
                }
            if (best != null && bestDist <= s.TextMaxDistance) res.Notes[best].Add(n);
        }
        return res;
    }

    private static void AddClone(List<EntityObject> list, EntityObject e)
    {
        if (e.Clone() is EntityObject copy) list.Add(copy);
    }

    private static List<EntityObject> DropOutliers(List<EntityObject> items)
    {
        var cx = new List<double>();
        var cy = new List<double>();
        foreach (var e in items)
        {
            var b = BoxOf(e);
            if (!b.Valid) continue;
            cx.Add((b.MinX + b.MaxX) / 2);
            cy.Add((b.MinY + b.MaxY) / 2);
        }
        if (cx.Count < 10) return items;
        cx.Sort();
        cy.Sort();
        double P(List<double> v, double q) => v[(int)Math.Round((v.Count - 1) * q)];
        double x0 = P(cx, 0.1), x1 = P(cx, 0.9), y0 = P(cy, 0.1), y1 = P(cy, 0.9);
        double sx = Math.Max(x1 - x0, 1), sy = Math.Max(y1 - y0, 1);
        var res = new List<EntityObject>();
        foreach (var e in items)
        {
            var b = BoxOf(e);
            if (!b.Valid) { res.Add(e); continue; }
            var mx = (b.MinX + b.MaxX) / 2;
            var my = (b.MinY + b.MaxY) / 2;
            if (mx < x0 - sx || mx > x1 + sx || my < y0 - sy || my > y1 + sy) continue;
            res.Add(e);
        }
        return res.Count > 0 ? res : items;
    }

    // ----------------------------------------------------------------- sheet

    private static int BuildSheet(DxfDocument doc, List<EntityObject> items, string partName, string drgNo, string gaFile)
    {
        items = DropOutliers(items);
        var bounds = new Box();
        foreach (var e in items)
            if (e is not netDxf.Entities.Point) bounds.Add(BoxOf(e));
        if (!bounds.Valid) foreach (var e in items) bounds.Add(BoxOf(e));
        if (!bounds.Valid) { bounds.Add(0, 0); bounds.Add(100, 100); }
        var w = bounds.MaxX - bounds.MinX;
        var h = bounds.MaxY - bounds.MinY;
        var sc = Math.Max(Math.Max(w, h), 1.0);
        var margin = sc * 0.04;
        var th = Math.Min(sc * 0.012, Math.Max(w, 1.0) / 40);
        var tbH = th * 1.8 * 6 + th;
        if (gaFile.Length > 38) gaFile = gaFile.Substring(0, 35) + "...";

        var shift = new Vector3(margin - bounds.MinX, tbH + margin - bounds.MinY, 0);
        foreach (var e in items)
        {
            e.TransformBy(Matrix3.Identity, shift);
            doc.Entities.Add(e);
        }

        var sheetW = w + 2 * margin;
        var sheetH = h + 2 * margin + tbH;
        var layer = new netDxf.Tables.Layer("TITLEBLOCK");

        void Rect(double x0, double y0, double x1, double y1)
        {
            var pl = new Polyline2D(new List<Vector2>
            {
                new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1)
            }, true);
            pl.Layer = layer;
            doc.Entities.Add(pl);
        }

        void Label(string text, double x, double y)
        {
            var t = new Text(text, new Vector2(x, y), th);
            t.Layer = layer;
            doc.Entities.Add(t);
        }

        Rect(0, 0, sheetW, sheetH);
        Rect(0, 0, sheetW, tbH);
        var tx = Math.Min(margin * 0.5, th);
        var rows = new[]
        {
            $"PART: {partName}",
            $"DRG NO: {drgNo}",
            $"GA FILE: {gaFile}",
            "SCALE: NTS",
            "MATERIAL:",
            $"DATE: {DateTime.UtcNow:yyyy-MM-dd}"
        };
        for (var i = 0; i < rows.Length; i++)
            Label(rows[i], tx, tbH - th * 0.5 - th * 1.8 * (i + 1) + th * 0.8);

        return items.Count;
    }

    private static string Sanitize(string name)
    {
        return Regex.Replace(name, @"[^A-Za-z0-9_.\-]+", "_").Trim('_');
    }
}