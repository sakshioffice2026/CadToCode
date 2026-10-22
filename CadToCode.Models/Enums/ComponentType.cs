namespace CadToCode.Models.Enums;

public enum ComponentType
{
    Line = 1,
    Arc = 2,
    Circle = 3,
    Hatch = 4,
    Cylinder = 5,
    Cone = 6,
    Door = 7,
    Window = 8,
    Duct = 9,
    AxialFan = 10,
    Cyclone = 11,
    Generic = 12,
    Head = 13,
    Jamb = 14,
    Mullion = 15,
    Sill = 16,
    Bracket = 17,
    Flange = 18,
    Panel = 19,
    CustomProfile = 20,
    CentrifugalFan = 21
}

public static class ComponentTypeExtensions
{
    public static bool IsProfile(this ComponentType type)
    {
        return type is ComponentType.Head
            or ComponentType.Jamb
            or ComponentType.Mullion
            or ComponentType.Sill
            or ComponentType.Bracket
            or ComponentType.Flange
            or ComponentType.Panel
            or ComponentType.CustomProfile;
    }
}