using CadToCode.Core.Calculators;
using CadToCode.Core.Knowledge;
using CadToCode.Core.Manifests;
using CadToCode.Core.Mapping;
using CadToCode.Core.Rules;
using CadToCode.Core.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CadToCode.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Core engineering services: design rules, calculators,
    /// local knowledge retrieval and the design workflow engine.
    /// Callers must also register <c>IDesignSessionStore</c> (Repositories)
    /// and, optionally, an <c>IWebSearchService</c> implementation (LLM).
    /// </summary>
    public static IServiceCollection AddCadToCodeCore(
        this IServiceCollection services,
        Action<EngineeringKnowledgeOptions>? configureKnowledge = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var knowledgeOptions = new EngineeringKnowledgeOptions();
        configureKnowledge?.Invoke(knowledgeOptions);

        services.TryAddSingleton(knowledgeOptions);

        services.TryAddSingleton<IDesignRulesProvider>(sp =>
            new JsonDesignRulesProvider(sp.GetRequiredService<EngineeringKnowledgeOptions>().RootPath));

        services.TryAddSingleton<IEquipmentManifestRegistry>(sp =>
            new JsonEquipmentManifestRegistry(sp.GetRequiredService<EngineeringKnowledgeOptions>().RootPath));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEquipmentCalculator, AxialFanCalculator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEquipmentCalculator, CentrifugalFanCalculator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEquipmentCalculator, WetScrubberCalculator>());
        services.TryAddSingleton<EquipmentCalculatorRegistry>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICalculatorTemplateMapper, CentrifugalFanTemplateMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICalculatorTemplateMapper, WetScrubberTemplateMapper>());
        services.TryAddSingleton<ICalculatorTemplateMapperRegistry, CalculatorTemplateMapperRegistry>();

        services.TryAddSingleton<IEngineeringKnowledgeService, EngineeringKnowledgeService>();

        services.TryAddScoped<IDesignWorkflowEngine, DesignWorkflowEngine>();

        return services;
    }
}