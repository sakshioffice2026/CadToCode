using System.Globalization;
using System.Text;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Enums;
using CadToCode.Repositories.Interfaces;

namespace CadToCode.Business.Services;

public class CodeGenerationService : ICodeGenerationService
{
    private readonly IUnitOfWork _unitOfWork;

    public CodeGenerationService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<string> GenerateAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var components = await _unitOfWork.Components.GetByProjectIdAsync(projectId, cancellationToken);

        var supported = components
            .Where(c => IsSupported(c.ComponentType))
            .OrderBy(c => c.SequenceIndex)
            .ToList();

        if (supported.Count == 0)
        {
            return "// No supported components were found for this project.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("// All dimensions in millimeters, all angles in degrees.");
        builder.AppendLine();

        var typeCounts = supported
            .GroupBy(c => c.ComponentType)
            .ToDictionary(g => g.Key, g => g.Count());

        var typeIndexes = new Dictionary<ComponentType, int>();

        for (var i = 0; i < supported.Count; i++)
        {
            var component = supported[i];

            typeIndexes.TryGetValue(component.ComponentType, out var index);
            index++;
            typeIndexes[component.ComponentType] = index;

            var className = typeCounts[component.ComponentType] > 1
                ? $"{component.ComponentType}{index}"
                : component.ComponentType.ToString();

            AppendClass(builder, className, component);

            if (i < supported.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static bool IsSupported(ComponentType type)
    {
        return type is not (ComponentType.Line or ComponentType.Arc or ComponentType.Circle or ComponentType.Hatch);
    }

    private static void AppendClass(StringBuilder builder, string className, ComponentDto component)
    {
        builder.AppendLine($"public class {className}");
        builder.AppendLine("{");

        switch (component.ComponentType)
        {
            case ComponentType.Cylinder:
                AppendProperty(builder, "Height", component.Height);
                AppendProperty(builder, "Diameter", component.Diameter);
                break;

            case ComponentType.Cone:
                AppendProperty(builder, "Angle", component.Angle);
                AppendProperty(builder, "TopDiameter", component.TopDiameter);
                AppendProperty(builder, "BottomDiameter", component.BottomDiameter);
                AppendProperty(builder, "Height", component.Height);
                break;

            case ComponentType.Door:
            case ComponentType.Window:
            case ComponentType.Duct:
                AppendProperty(builder, "Width", component.Width);
                AppendProperty(builder, "Height", component.Height);
                break;

            case ComponentType.AxialFan:
                AppendProperty(builder, "Diameter", component.Diameter);
                break;

            case ComponentType.Cyclone:
                AppendProperty(builder, "Height", component.Height);
                AppendProperty(builder, "Diameter", component.Diameter);
                AppendProperty(builder, "TopDiameter", component.TopDiameter);
                AppendProperty(builder, "BottomDiameter", component.BottomDiameter);
                AppendProperty(builder, "Angle", component.Angle);
                break;
        }

        builder.AppendLine($"    public bool HasHatch {{ get; set; }} = {(component.HasHatch ? "true" : "false")};");
        builder.AppendLine("}");
    }

    private static void AppendProperty(StringBuilder builder, string name, decimal? value)
    {
        if (value is null)
        {
            return;
        }

        var text = value.Value.ToString("0.####", CultureInfo.InvariantCulture);
        builder.AppendLine($"    public double {name} {{ get; set; }} = {text};");
    }
}