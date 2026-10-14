using System.Globalization;

namespace CadToCode.Step;

public readonly struct Vec3 : IEquatable<Vec3>
{
    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public static Vec3 Zero => new(0, 0, 0);

    public static Vec3 UnitX => new(1, 0, 0);

    public static Vec3 UnitY => new(0, 1, 0);

    public static Vec3 UnitZ => new(0, 0, 1);

    public double LengthSquared => X * X + Y * Y + Z * Z;

    public double Length => Math.Sqrt(LengthSquared);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);

    public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

    public double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vec3 Cross(Vec3 other)
    {
        return new Vec3(
            Y * other.Z - Z * other.Y,
            Z * other.X - X * other.Z,
            X * other.Y - Y * other.X);
    }

    public double DistanceTo(Vec3 other) => (this - other).Length;

    public Vec3 Normalized()
    {
        var length = Length;

        if (!double.IsFinite(length) || length <= Frame3D.Epsilon)
        {
            throw new InvalidOperationException("A zero-length or non-finite vector cannot be normalized.");
        }

        return this / length;
    }

    public bool IsApproximately(Vec3 other, double tolerance = 1e-9)
    {
        return Math.Abs(X - other.X) <= tolerance
            && Math.Abs(Y - other.Y) <= tolerance
            && Math.Abs(Z - other.Z) <= tolerance;
    }

    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({X:0.######}, {Y:0.######}, {Z:0.######})");
    }
}

public sealed class Frame3D
{
    internal const double Epsilon = 1e-12;

    private Frame3D(Vec3 origin, Vec3 xAxis, Vec3 yAxis, Vec3 zAxis)
    {
        Origin = origin;
        XAxis = xAxis;
        YAxis = yAxis;
        ZAxis = zAxis;
    }

    public Frame3D(Vec3 origin, Vec3 zAxis, Vec3 xHint)
    {
        RequireFinite(origin, nameof(origin));
        RequireFinite(zAxis, nameof(zAxis));
        RequireFinite(xHint, nameof(xHint));

        if (zAxis.Length <= Epsilon)
        {
            throw new ArgumentException("The Z axis must not be a zero-length vector.", nameof(zAxis));
        }

        var z = zAxis.Normalized();
        var x = ResolveXAxis(z, xHint);

        Origin = origin;
        ZAxis = z;
        XAxis = x;
        YAxis = z.Cross(x).Normalized();
    }

    public Vec3 Origin { get; }

    public Vec3 XAxis { get; }

    public Vec3 YAxis { get; }

    public Vec3 ZAxis { get; }

    public static Frame3D World { get; } = new(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);

    public static Frame3D At(double x, double y, double z)
    {
        return World.Translate(x, y, z);
    }

    public static Frame3D At(Vec3 origin)
    {
        return World.Translate(origin);
    }

    public static Frame3D FromZ(Vec3 origin, Vec3 zAxis)
    {
        return new Frame3D(origin, zAxis, PerpendicularHint(zAxis));
    }

    public static Frame3D FromAxes(Vec3 origin, Vec3 xAxis, Vec3 zAxis)
    {
        return new Frame3D(origin, zAxis, xAxis);
    }

    public Frame3D Translate(Vec3 offset)
    {
        RequireFinite(offset, nameof(offset));
        return new Frame3D(Origin + offset, XAxis, YAxis, ZAxis);
    }

    public Frame3D Translate(double dx, double dy, double dz)
    {
        return Translate(new Vec3(dx, dy, dz));
    }

    public Frame3D TranslateLocal(double dx, double dy, double dz)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dz))
        {
            throw new ArgumentOutOfRangeException(nameof(dx), "The offset must be made of finite numbers.");
        }

        return new Frame3D(Origin + XAxis * dx + YAxis * dy + ZAxis * dz, XAxis, YAxis, ZAxis);
    }

    public Frame3D Rotate(Vec3 axisDirection, double angleDegrees)
    {
        return RotateAbout(Origin, axisDirection, angleDegrees);
    }

    public Frame3D RotateAbout(Vec3 pivot, Vec3 axisDirection, double angleDegrees)
    {
        RequireFinite(pivot, nameof(pivot));
        RequireFinite(axisDirection, nameof(axisDirection));

        if (!double.IsFinite(angleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(angleDegrees), "The angle must be a finite number.");
        }

        if (axisDirection.Length <= Epsilon)
        {
            throw new ArgumentException("The rotation axis must not be a zero-length vector.", nameof(axisDirection));
        }

        var axis = axisDirection.Normalized();
        var radians = angleDegrees * Math.PI / 180.0;

        var origin = pivot + RotateVector(Origin - pivot, axis, radians);
        var x = RotateVector(XAxis, axis, radians).Normalized();
        var y = RotateVector(YAxis, axis, radians).Normalized();
        var z = RotateVector(ZAxis, axis, radians).Normalized();

        return new Frame3D(origin, x, y, z);
    }

    public Frame3D RotateWorldX(double angleDegrees) => Rotate(Vec3.UnitX, angleDegrees);

    public Frame3D RotateWorldY(double angleDegrees) => Rotate(Vec3.UnitY, angleDegrees);

    public Frame3D RotateWorldZ(double angleDegrees) => Rotate(Vec3.UnitZ, angleDegrees);

    public Frame3D RotateLocalX(double angleDegrees) => Rotate(XAxis, angleDegrees);

    public Frame3D RotateLocalY(double angleDegrees) => Rotate(YAxis, angleDegrees);

    public Frame3D RotateLocalZ(double angleDegrees) => Rotate(ZAxis, angleDegrees);

    public Frame3D Mirror(Vec3 planePoint, Vec3 planeNormal)
    {
        RequireFinite(planePoint, nameof(planePoint));
        RequireFinite(planeNormal, nameof(planeNormal));

        if (planeNormal.Length <= Epsilon)
        {
            throw new ArgumentException("The mirror plane normal must not be a zero-length vector.", nameof(planeNormal));
        }

        var normal = planeNormal.Normalized();

        var origin = ReflectPoint(Origin, planePoint, normal);
        var x = ReflectDirection(XAxis, normal).Normalized();
        var z = ReflectDirection(ZAxis, normal).Normalized();
        var y = z.Cross(x).Normalized();

        return new Frame3D(origin, x, y, z);
    }

    public Frame3D MirrorXY(double planeZ = 0) => Mirror(new Vec3(0, 0, planeZ), Vec3.UnitZ);

    public Frame3D MirrorXZ(double planeY = 0) => Mirror(new Vec3(0, planeY, 0), Vec3.UnitY);

    public Frame3D MirrorYZ(double planeX = 0) => Mirror(new Vec3(planeX, 0, 0), Vec3.UnitX);

    public Frame3D Compose(Frame3D child)
    {
        if (child is null)
        {
            throw new ArgumentNullException(nameof(child));
        }

        return new Frame3D(
            ToWorld(child.Origin),
            ToWorldDirection(child.XAxis).Normalized(),
            ToWorldDirection(child.YAxis).Normalized(),
            ToWorldDirection(child.ZAxis).Normalized());
    }

    public Vec3 ToWorld(Vec3 local)
    {
        return Origin + XAxis * local.X + YAxis * local.Y + ZAxis * local.Z;
    }

    public Vec3 ToWorld(double x, double y, double z)
    {
        return ToWorld(new Vec3(x, y, z));
    }

    public Vec3 ToWorldDirection(Vec3 local)
    {
        return XAxis * local.X + YAxis * local.Y + ZAxis * local.Z;
    }

    public Vec3 ToLocal(Vec3 world)
    {
        var d = world - Origin;
        return new Vec3(d.Dot(XAxis), d.Dot(YAxis), d.Dot(ZAxis));
    }

    public Vec3 ToLocalDirection(Vec3 world)
    {
        return new Vec3(world.Dot(XAxis), world.Dot(YAxis), world.Dot(ZAxis));
    }

    public bool IsApproximately(Frame3D other, double tolerance = 1e-9)
    {
        if (other is null)
        {
            return false;
        }

        return Origin.IsApproximately(other.Origin, tolerance)
            && XAxis.IsApproximately(other.XAxis, tolerance)
            && YAxis.IsApproximately(other.YAxis, tolerance)
            && ZAxis.IsApproximately(other.ZAxis, tolerance);
    }

    public override string ToString()
    {
        return $"Frame3D(O={Origin}, X={XAxis}, Y={YAxis}, Z={ZAxis})";
    }

    private static Vec3 ResolveXAxis(Vec3 unitZ, Vec3 xHint)
    {
        var candidate = xHint - unitZ * xHint.Dot(unitZ);

        if (candidate.Length <= 1e-9)
        {
            candidate = PerpendicularHint(unitZ);
            candidate -= unitZ * candidate.Dot(unitZ);
        }

        return candidate.Normalized();
    }

    private static Vec3 PerpendicularHint(Vec3 axis)
    {
        var ax = Math.Abs(axis.X);
        var ay = Math.Abs(axis.Y);
        var az = Math.Abs(axis.Z);

        if (ax <= ay && ax <= az)
        {
            return Vec3.UnitX;
        }

        return ay <= az ? Vec3.UnitY : Vec3.UnitZ;
    }

    private static Vec3 RotateVector(Vec3 v, Vec3 unitAxis, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        return v * cos + unitAxis.Cross(v) * sin + unitAxis * (unitAxis.Dot(v) * (1.0 - cos));
    }

    private static Vec3 ReflectPoint(Vec3 point, Vec3 planePoint, Vec3 unitNormal)
    {
        var distance = (point - planePoint).Dot(unitNormal);
        return point - unitNormal * (2.0 * distance);
    }

    private static Vec3 ReflectDirection(Vec3 direction, Vec3 unitNormal)
    {
        return direction - unitNormal * (2.0 * direction.Dot(unitNormal));
    }

    private static void RequireFinite(Vec3 value, string parameter)
    {
        if (!value.IsFinite)
        {
            throw new ArgumentException("The vector must be made of finite numbers.", parameter);
        }
    }
}