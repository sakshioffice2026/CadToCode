using CadToCode.Models.Dtos;

namespace CadToCode.LLM.Templates;

public static class BuiltInCadTemplates
{
    public static IReadOnlyList<CadTemplateDefinition> All { get; } = new List<CadTemplateDefinition>
    {
        new()
        {
            Key = "builtin-straight-pipe",
            Title = "Straight pipe",
            Category = "Pipe",
            Tags = new List<string> { "pipe", "tube", "duct", "straight", "hollow", "wall", "outer diameter", "length" },
            Description = "Hollow straight pipe swept along Z from outer diameter, wall thickness and length.",
            Script = StraightPipe
        },
        new()
        {
            Key = "builtin-pipe-elbow",
            Title = "Pipe elbow",
            Category = "Elbow",
            Tags = new List<string> { "elbow", "bend", "pipe", "90", "angle", "radius", "tube", "duct", "fitting" },
            Description = "Hollow pipe with a straight leg, a tangent bend and a second straight leg.",
            Script = PipeElbow
        },
        new()
        {
            Key = "builtin-pipe-tee",
            Title = "Pipe tee",
            Category = "Pipe",
            Tags = new List<string> { "tee", "branch", "junction", "pipe", "t-piece", "fitting", "header" },
            Description = "Main pipe run along Z with a branch pipe along X that starts inside the run.",
            Script = PipeTee
        },
        new()
        {
            Key = "builtin-flange-plate",
            Title = "Flange with bolt circle and neck",
            Category = "Flange",
            Tags = new List<string> { "flange", "plate", "bolt", "bolt circle", "holes", "bore", "neck", "ring", "disc" },
            Description = "Round flange plate with a central bore, bolt holes on a bolt circle and a pipe neck.",
            Script = FlangePlate
        },
        new()
        {
            Key = "builtin-wet-scrubber",
            Title = "Wet scrubber vessel",
            Category = "Scrubber",
            Tags = new List<string> { "scrubber", "wet scrubber", "vessel", "tower", "column", "tank", "absorber", "stack", "inlet", "drain", "cone", "gas cleaning" },
            Description = "Vertical cylindrical scrubber with a conical bottom, top cover, gas outlet stack, side inlet and drain pipe.",
            Script = WetScrubber
        },
        new()
        {
            Key = "builtin-wet-scrubber-tank-pump",
            Title = "Wet scrubber with tank, pump and spray header",
            Category = "Scrubber",
            Tags = new List<string> { "scrubber", "wet scrubber", "packed tower", "packing bed", "spray header", "spray nozzle", "recirculation", "recirculation tank", "sump", "pump", "motor", "suction", "discharge", "inlet", "outlet", "stack", "vessel", "tank", "gas cleaning" },
            Description = "Vertical packed scrubber on a rectangular recirculation tank with a packing bed, spray header with nozzles, gas inlet and outlet stack, drain, and a pump connected by suction and discharge pipes.",
            Script = WetScrubberWithTankAndPump
        },
        new()
        {
            Key = "builtin-cyclone-separator",
            Title = "Cyclone separator",
            Category = "Cyclone",
            Tags = new List<string> { "cyclone", "separator", "dust", "collector", "vortex", "tangential", "inlet", "cone", "hopper", "dedusting" },
            Description = "Cylindrical cyclone with a conical hopper, tangential inlet, vortex finder and dust outlet.",
            Script = CycloneSeparator
        }
    };

    public static CadTemplateDefinition? Find(string key)
    {
        return All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private const string StraightPipe = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double OuterDiameter = 100;
const double Wall = 5;
const double Length = 1000;

var doc = new StepDocument("StraightPipe");
doc.Add.Pipe("Pipe", SweepRoute.From(0, 0, 0).LineTo(0, 0, Length), OuterDiameter, Wall);

var outputPath = Path.GetFullPath("StraightPipe.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string PipeElbow = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double OuterDiameter = 100;
const double Wall = 5;
const double LegLength = 200;
const double BendRadius = 150;
const double BendAngle = 90;

var doc = new StepDocument("PipeElbow");
var route = SweepRoute.From(0, 0, 0)
    .LineTo(0, 0, LegLength)
    .Bend(BendRadius, BendAngle, Vec3.UnitY)
    .Straight(LegLength);
doc.Add.Pipe("Elbow", route, OuterDiameter, Wall);

var outputPath = Path.GetFullPath("PipeElbow.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string PipeTee = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double RunDiameter = 150;
const double RunWall = 6;
const double RunLength = 600;
const double BranchDiameter = 100;
const double BranchWall = 5;
const double BranchLength = 300;

var doc = new StepDocument("PipeTee");
doc.Add.Pipe("Run", SweepRoute.From(0, 0, 0).LineTo(0, 0, RunLength), RunDiameter, RunWall);
doc.Add.Pipe(
    "Branch",
    SweepRoute.From(0, 0, RunLength / 2.0).LineTo(RunDiameter / 2.0 + BranchLength, 0, RunLength / 2.0),
    BranchDiameter,
    BranchWall);

var outputPath = Path.GetFullPath("PipeTee.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string FlangePlate = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double FlangeDiameter = 220;
const double FlangeThickness = 20;
const double BoreDiameter = 100;
const int BoltCount = 8;
const double BoltCircleDiameter = 180;
const double BoltHoleDiameter = 18;
const double NeckWall = 6;
const double NeckLength = 80;

var doc = new StepDocument("FlangePlate");

var holes = new List<ProfileLoop> { ProfileLoop.Circle(0, 0, BoreDiameter / 2.0) };
for (var i = 0; i < BoltCount; i++)
{
    var angle = 2.0 * Math.PI * i / BoltCount;
    holes.Add(ProfileLoop.Circle(
        BoltCircleDiameter / 2.0 * Math.Cos(angle),
        BoltCircleDiameter / 2.0 * Math.Sin(angle),
        BoltHoleDiameter / 2.0));
}

var plate = Profile2D.Create(ProfileLoop.Circle(0, 0, FlangeDiameter / 2.0), holes);
doc.Add.Extrude("Flange", plate, FlangeThickness);
doc.Add.Pipe(
    "Neck",
    SweepRoute.From(0, 0, FlangeThickness).LineTo(0, 0, FlangeThickness + NeckLength),
    BoreDiameter + 2.0 * NeckWall,
    NeckWall);

var outputPath = Path.GetFullPath("FlangePlate.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string WetScrubber = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double Radius = 600;
const double Wall = 8;
const double ShellHeight = 1800;
const double ConeHeight = 500;
const double DrainDiameter = 150;
const double DrainLength = 300;
const double StackDiameter = 300;
const double StackHeight = 400;
const double InletDiameter = 250;
const double InletLength = 400;
const double InletZ = 1500;

var doc = new StepDocument("WetScrubber");
var topZ = ConeHeight + ShellHeight;

var shell = Profile2D.Polygon(new List<(double X, double Y)>
{
    (DrainDiameter / 2.0, 0), (Radius, ConeHeight), (Radius, topZ),
    (Radius - Wall, topZ), (Radius - Wall, ConeHeight), (DrainDiameter / 2.0 - Wall, 0)
});
doc.Add.Revolve("Shell", shell);

var cover = Profile2D.Create(
    ProfileLoop.Circle(0, 0, Radius),
    new List<ProfileLoop> { ProfileLoop.Circle(0, 0, StackDiameter / 2.0 - Wall) });
doc.Add.Extrude("TopCover", cover, Wall, Frame3D.At(0, 0, topZ));

doc.Add.Pipe("Stack", SweepRoute.From(0, 0, topZ).LineTo(0, 0, topZ + Wall + StackHeight), StackDiameter, Wall);
doc.Add.Pipe("Drain", SweepRoute.From(0, 0, 0).LineTo(0, 0, -DrainLength), DrainDiameter, Wall);
doc.Add.Pipe("Inlet", SweepRoute.From(Radius - Wall, 0, InletZ).LineTo(Radius + InletLength, 0, InletZ), InletDiameter, Wall);

var outputPath = Path.GetFullPath("WetScrubber.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string WetScrubberWithTankAndPump = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double Radius = 600;
const double Wall = 8;
const double ShellHeight = 2400;
const double TankLength = 2000;
const double TankWidth = 1400;
const double TankHeight = 800;
const double TankWall = 8;
const double PackingHeight = 600;
const double PackingOffset = 700;
const double SprayGap = 200;
const double SprayDiameter = 80;
const double SprayWall = 4;
const double NozzleLength = 150;
const double FeedLength = 400;
const double StackDiameter = 300;
const double StackHeight = 400;
const double InletDiameter = 250;
const double InletLength = 400;
const double InletHeight = 250;
const double DrainDiameter = 100;
const double DrainLength = 250;
const double PumpRadius = 120;
const double PumpHeight = 250;
const double PumpGap = 200;
const double MotorRadius = 100;
const double MotorHeight = 300;
const double SuctionDiameter = 100;
const double DischargeDiameter = 80;
const double RiserOffset = 150;

var doc = new StepDocument("WetScrubberWithTankAndPump");

var halfLength = TankLength / 2.0;
var halfWidth = TankWidth / 2.0;
var shellBaseZ = TankHeight - TankWall;
var topZ = TankHeight + ShellHeight;
var packingZ = TankHeight + PackingOffset;
var sprayZ = packingZ + PackingHeight + SprayGap;
var inletZ = TankHeight + InletHeight;
var pumpX = halfLength + PumpGap + PumpRadius;
var riserX = pumpX + PumpRadius + RiserOffset;
var headerEndX = Radius + FeedLength;
var pumpZ = PumpHeight / 2.0;

var tankFloor = Profile2D.Rectangle(-halfLength, -halfWidth, TankLength, TankWidth);
doc.Add.Extrude("TankFloor", tankFloor, TankWall);

var tankWalls = Profile2D.Create(
    ProfileLoop.Rectangle(-halfLength, -halfWidth, TankLength, TankWidth),
    new List<ProfileLoop> { ProfileLoop.Rectangle(-halfLength + TankWall, -halfWidth + TankWall, TankLength - 2.0 * TankWall, TankWidth - 2.0 * TankWall) });
doc.Add.Extrude("TankWalls", tankWalls, TankHeight);

var tankRoof = Profile2D.Create(
    ProfileLoop.Rectangle(-halfLength, -halfWidth, TankLength, TankWidth),
    new List<ProfileLoop> { ProfileLoop.Circle(0, 0, Radius - Wall) });
doc.Add.Extrude("TankRoof", tankRoof, TankWall, Frame3D.At(0, 0, shellBaseZ));

var shell = Profile2D.Polygon(new List<(double X, double Y)>
{
    (Radius - Wall, shellBaseZ), (Radius, shellBaseZ), (Radius, topZ), (Radius - Wall, topZ)
});
doc.Add.Revolve("Shell", shell);

var packing = Profile2D.Circle(0, 0, Radius - Wall / 2.0);
doc.Add.Extrude("PackingBed", packing, PackingHeight, Frame3D.At(0, 0, packingZ));

var cover = Profile2D.Create(
    ProfileLoop.Circle(0, 0, Radius),
    new List<ProfileLoop> { ProfileLoop.Circle(0, 0, StackDiameter / 2.0 - Wall) });
doc.Add.Extrude("TopCover", cover, Wall, Frame3D.At(0, 0, topZ));

doc.Add.Pipe("Stack", SweepRoute.From(0, 0, topZ).LineTo(0, 0, topZ + Wall + StackHeight), StackDiameter, Wall);
doc.Add.Pipe("Inlet", SweepRoute.From(-(Radius - Wall), 0, inletZ).LineTo(-(Radius + InletLength), 0, inletZ), InletDiameter, Wall);
doc.Add.Pipe("Drain", SweepRoute.From(-halfLength / 2.0, 0, TankWall).LineTo(-halfLength / 2.0, 0, -DrainLength), DrainDiameter, TankWall);

doc.Add.Pipe("SprayHeader", SweepRoute.From(-(Radius - Wall / 2.0), 0, sprayZ).LineTo(headerEndX, 0, sprayZ), SprayDiameter, SprayWall);
doc.Add.Pipe("SprayNozzleA", SweepRoute.From(-Radius / 2.0, 0, sprayZ).LineTo(-Radius / 2.0, 0, sprayZ - NozzleLength), SprayDiameter / 2.0, SprayWall / 2.0);
doc.Add.Pipe("SprayNozzleB", SweepRoute.From(Radius / 2.0, 0, sprayZ).LineTo(Radius / 2.0, 0, sprayZ - NozzleLength), SprayDiameter / 2.0, SprayWall / 2.0);

doc.Add.Extrude("PumpCasing", Profile2D.Circle(0, 0, PumpRadius), PumpHeight, Frame3D.At(pumpX, 0, 0));
doc.Add.Extrude("PumpMotor", Profile2D.Circle(0, 0, MotorRadius), MotorHeight, Frame3D.At(pumpX, 0, PumpHeight));

doc.Add.Pipe("Suction", SweepRoute.From(halfLength - TankWall, 0, pumpZ).LineTo(pumpX, 0, pumpZ), SuctionDiameter, TankWall);
doc.Add.Pipe("DischargeRun", SweepRoute.From(pumpX, 0, pumpZ).LineTo(riserX, 0, pumpZ), DischargeDiameter, SprayWall);
doc.Add.Pipe("DischargeRiser", SweepRoute.From(riserX, 0, pumpZ).LineTo(riserX, 0, sprayZ), DischargeDiameter, SprayWall);
doc.Add.Pipe("DischargeFeed", SweepRoute.From(riserX, 0, sprayZ).LineTo(headerEndX - SprayDiameter, 0, sprayZ), DischargeDiameter, SprayWall);

var outputPath = Path.GetFullPath("WetScrubberWithTankAndPump.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";

    private const string CycloneSeparator = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadToCode.Step;

const double Radius = 300;
const double Wall = 6;
const double BodyHeight = 600;
const double ConeHeight = 900;
const double DustDiameter = 100;
const double DustLength = 150;
const double ExitDiameter = 150;
const double ExitHeight = 250;
const double FinderDepth = 350;
const double InletDiameter = 120;
const double InletLength = 300;

var doc = new StepDocument("CycloneSeparator");
var topZ = ConeHeight + BodyHeight;
var inletZ = topZ - InletDiameter;

var body = Profile2D.Polygon(new List<(double X, double Y)>
{
    (DustDiameter / 2.0, 0), (Radius, ConeHeight), (Radius, topZ),
    (Radius - Wall, topZ), (Radius - Wall, ConeHeight), (DustDiameter / 2.0 - Wall, 0)
});
doc.Add.Revolve("Body", body);

var plate = Profile2D.Create(
    ProfileLoop.Circle(0, 0, Radius),
    new List<ProfileLoop> { ProfileLoop.Circle(0, 0, ExitDiameter / 2.0 - Wall) });
doc.Add.Extrude("TopPlate", plate, Wall, Frame3D.At(0, 0, topZ));

doc.Add.Pipe("VortexFinder", SweepRoute.From(0, 0, topZ - FinderDepth).LineTo(0, 0, topZ + Wall + ExitHeight), ExitDiameter, Wall);
doc.Add.Pipe("DustOutlet", SweepRoute.From(0, 0, 0).LineTo(0, 0, -DustLength), DustDiameter, Wall);
doc.Add.Pipe(
    "TangentialInlet",
    SweepRoute.From(Radius - InletDiameter / 2.0, InletLength, inletZ).LineTo(Radius - InletDiameter / 2.0, 0, inletZ),
    InletDiameter,
    Wall);

var outputPath = Path.GetFullPath("CycloneSeparator.step");
doc.Save(outputPath);
Console.WriteLine("STEP saved to " + outputPath + " (" + doc.SolidCount + " solids)");
""";
}