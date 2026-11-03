using System.Globalization;
using System.Text.RegularExpressions;
using CadToCode.Core.Rules;

namespace CadToCode.Business.Services
{
    public sealed class GaReferencePart
    {
        public string Name { get; init; } = string.Empty;

        public double? ThicknessMm { get; init; }

        public int? Qty { get; init; }

        public double? WeightKg { get; init; }
    }

    public sealed class GaReferenceCase
    {
        public string DrawingNo { get; init; } = string.Empty;

        public int PartCount { get; init; }

        public double? TotalKg { get; init; }

        public List<GaReferencePart> Parts { get; init; } = new();
    }

    public sealed class GaReferenceRow
    {
        public string Part { get; init; } = string.Empty;

        public string Check { get; init; } = string.Empty;

        public string Expected { get; init; } = string.Empty;

        public string Actual { get; init; } = string.Empty;

        public bool Passed { get; init; }
    }

    public sealed class GaReferenceResult
    {
        public string DrawingNo { get; init; } = string.Empty;

        public List<GaReferenceRow> Rows { get; } = new();

        public bool Passed => Rows.Count > 0 && Rows.All(r => r.Passed);

        public int Failed => Rows.Count(r => !r.Passed);

        public List<string> ToMessages()
        {
            var list = new List<string>
            {
                (Passed ? "PASS " : "FAIL ") + DrawingNo + ": "
                + (Rows.Count - Failed).ToString(CultureInfo.InvariantCulture) + " of "
                + Rows.Count.ToString(CultureInfo.InvariantCulture) + " checks passed."
            };

            foreach (var row in Rows.Where(r => !r.Passed))
            {
                list.Add("  " + row.Part + " [" + row.Check + "] expected " + row.Expected + ", got " + row.Actual + ".");
            }

            return list;
        }
    }

    public sealed class GaReferenceCheck
    {
        private const double ThicknessToleranceMm = 0.01;

        private static readonly Regex NonAlphaNumericRx = new(@"[^A-Z0-9]+", RegexOptions.Compiled);

        public GaReferenceResult Compare(GaReferenceCase reference, GaPipelineResult result, ManufacturingRules rules)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(rules);

            var check = new GaReferenceResult { DrawingNo = reference.DrawingNo };
            var callouts = result.Callouts.Parts;

            check.Rows.Add(new GaReferenceRow
            {
                Part = "ALL PARTS",
                Check = "PART COUNT",
                Expected = reference.PartCount.ToString(CultureInfo.InvariantCulture),
                Actual = callouts.Count.ToString(CultureInfo.InvariantCulture),
                Passed = callouts.Count == reference.PartCount
            });

            foreach (var expected in reference.Parts)
            {
                var key = Key(expected.Name);
                var found = callouts.FirstOrDefault(p => Key(p.Name) == key);

                if (found is null)
                {
                    check.Rows.Add(new GaReferenceRow
                    {
                        Part = expected.Name,
                        Check = "FOUND",
                        Expected = "callout present",
                        Actual = "missing",
                        Passed = false
                    });
                    continue;
                }

                var disposition = result.Completeness.Rows.FirstOrDefault(r => Key(r.Part.Name) == key);

                check.Rows.Add(new GaReferenceRow
                {
                    Part = expected.Name,
                    Check = "DISPOSITION",
                    Expected = "DXF, cut list or bought-out",
                    Actual = disposition is null ? "none" : disposition.Disposition.ToString(),
                    Passed = disposition is not null && disposition.Disposition != PartDisposition.Unmatched
                });

                if (expected.ThicknessMm.HasValue)
                {
                    var actual = found.ThicknessMm;

                    check.Rows.Add(new GaReferenceRow
                    {
                        Part = expected.Name,
                        Check = "THK",
                        Expected = Number(expected.ThicknessMm.Value),
                        Actual = actual.HasValue ? Number(actual.Value) : "none",
                        Passed = actual.HasValue && Math.Abs(actual.Value - expected.ThicknessMm.Value) <= ThicknessToleranceMm
                    });
                }

                if (expected.Qty.HasValue)
                {
                    check.Rows.Add(new GaReferenceRow
                    {
                        Part = expected.Name,
                        Check = "QTY",
                        Expected = expected.Qty.Value.ToString(CultureInfo.InvariantCulture),
                        Actual = found.Qty.ToString(CultureInfo.InvariantCulture),
                        Passed = found.Qty == expected.Qty.Value
                    });
                }

                if (expected.WeightKg.HasValue)
                {
                    var actual = found.WeightKg;

                    check.Rows.Add(new GaReferenceRow
                    {
                        Part = expected.Name,
                        Check = "WEIGHT",
                        Expected = Number(expected.WeightKg.Value) + " kg",
                        Actual = actual.HasValue ? Number(actual.Value) + " kg" : "none",
                        Passed = actual.HasValue && rules.IsWithinReference(actual.Value, expected.WeightKg.Value)
                    });
                }
            }

            if (reference.TotalKg.HasValue)
            {
                var calculated = result.CalculatedFinishTotalKg;

                check.Rows.Add(new GaReferenceRow
                {
                    Part = "ALL PARTS",
                    Check = "TOTAL WEIGHT",
                    Expected = Number(reference.TotalKg.Value) + " kg",
                    Actual = Number(calculated) + " kg",
                    Passed = rules.IsWithinReference(calculated, reference.TotalKg.Value)
                });
            }

            return check;
        }

        public IReadOnlyList<GaReferenceResult> RunAll(
            IEnumerable<(GaReferenceCase Reference, GaPipelineRequest Request)> cases,
            GaManufacturingPipeline pipeline,
            ManufacturingRules rules)
        {
            ArgumentNullException.ThrowIfNull(cases);
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(rules);

            var results = new List<GaReferenceResult>();

            foreach (var item in cases)
            {
                var run = pipeline.Run(item.Request);
                results.Add(Compare(item.Reference, run, rules));
            }

            return results;
        }

        private static string Key(string? name)
        {
            return NonAlphaNumericRx.Replace((name ?? string.Empty).ToUpperInvariant(), string.Empty);
        }

        private static string Number(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
