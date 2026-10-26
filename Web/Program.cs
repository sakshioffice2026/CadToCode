using CadToCode.Business.Interfaces;
using CadToCode.Business.Sandbox;
using CadToCode.Business.Services;
using CadToCode.Core;
using CadToCode.Core.Calculators;
using CadToCode.Core.Rules;
using CadToCode.Core.Services;
using CadToCode.Database;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Services;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using CadToCode.Repositories.Interfaces;
using CadToCode.Repositories.Repository;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.Configure<ZooOptions>(builder.Configuration.GetSection(ZooOptions.SectionName));
builder.Services.AddScoped<IZooGenerationRepository, ZooGenerationRepository>();
builder.Services.AddHttpClient<IZooTextToCadService, ZooTextToCadService>();

builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));
builder.Services.PostConfigure<UploadOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.StorageRoot))
    {
        options.StorageRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "uploads");
    }
});
builder.Services.Configure<ExtractionOptions>(builder.Configuration.GetSection(ExtractionOptions.SectionName));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.Configure<LocalAiOptions>(builder.Configuration.GetSection(LocalAiOptions.SectionName));
builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection(VisionOptions.SectionName));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.SectionName));
builder.Services.Configure<GeminiEmbeddingOptions>(builder.Configuration.GetSection(GeminiEmbeddingOptions.SectionName));

builder.Services.Configure<TavilyOptions>(builder.Configuration.GetSection(TavilyOptions.SectionName));
builder.Services.Configure<DuckDuckGoOptions>(builder.Configuration.GetSection(DuckDuckGoOptions.SectionName));

builder.Services.AddCadToCodeCore();
builder.Services.AddScoped<IDesignSessionStore, DesignSessionStore>();

// Core registers the concrete EquipmentCalculatorRegistry only; expose it through its interface.
builder.Services.AddSingleton<IEquipmentCalculatorRegistry>(sp => sp.GetRequiredService<EquipmentCalculatorRegistry>());

// Part script builders: fans build from script composers, cyclone and wet scrubber build from manifest templates.
builder.Services.AddSingleton<IPartScriptBuilder, CentrifugalFanPartScriptBuilder>();
builder.Services.AddSingleton<IPartScriptBuilder, AxialFanPartScriptBuilder>();
builder.Services.AddSingleton<IPartScriptBuilder, TemplatePartScriptBuilder>();
builder.Services.AddSingleton<IPartScriptBuilderRegistry, PartScriptBuilderRegistry>();

// One design flow per equipment type, each with its own rule pack and prompts.
builder.Services.AddScoped<IEquipmentDesignFlow, CentrifugalFanDesignFlow>();
builder.Services.AddScoped<IEquipmentDesignFlow, AxialFanDesignFlow>();
builder.Services.AddScoped<IEquipmentDesignFlow, CycloneDesignFlow>();
builder.Services.AddScoped<IEquipmentDesignFlow, WetScrubberDesignFlow>();
builder.Services.AddScoped<IEquipmentDesignDispatcher, EquipmentDesignDispatcher>();

// Web search is advisory only. Tavily is used when configured, otherwise DuckDuckGo.
builder.Services.AddHttpClient<TavilyWebSearchService>();
builder.Services.AddHttpClient<DuckDuckGoWebSearchService>();
builder.Services.AddScoped<IWebSearchService>(sp =>
{
    var tavily = sp.GetRequiredService<TavilyWebSearchService>();
    return tavily.IsAvailable ? tavily : sp.GetRequiredService<DuckDuckGoWebSearchService>();
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
builder.Services.AddSingleton<IDxfCompatibilityService, DxfCompatibilityService>();
builder.Services.AddSingleton<IGeometryExtractor, DxfGeometryExtractor>();

// Local OCR is a best-effort service. ImageGeometryExtractor falls back to visual-only
// feature detection when Tesseract or its local eng.traineddata is unavailable.
builder.Services.AddSingleton<IImageTextRecognizer, TesseractTextRecognizer>();
builder.Services.AddSingleton<IGeometryExtractor, ImageGeometryExtractor>();

builder.Services.AddScoped<IGeometryNormalizer, GeometryNormalizer>();
builder.Services.AddSingleton<IComponentDecomposer, ComponentDecomposer>();
builder.Services.AddSingleton<IAssemblyTopologyMapper, AssemblyTopologyMapper>();
builder.Services.AddSingleton<GeometryEmbedder>();
builder.Services.AddSingleton<IPatternMemoryService, PatternMemoryService>();
builder.Services.AddSingleton<ILLMGenerationService, LLamaGenerationService>();
builder.Services.AddSingleton<IChatMemoryStore, ChatMemoryStore>();
builder.Services.AddSingleton<ICadAssistSessionStore, CadAssistSessionStore>();
builder.Services.AddSingleton<ILocalAiService, LocalAiService>();
builder.Services.AddSingleton<IImageOptimizer, ImageOptimizer>();
builder.Services.AddSingleton<IVisionInspectionService, NativeVisionService>();
builder.Services.AddScoped<IAiComponentClassifier, AiComponentClassifier>();
builder.Services.AddScoped<ICodeGenerationService, CodeGenerationService>();
builder.Services.AddScoped<IDefinitionLearningService, DefinitionLearningService>();
builder.Services.AddScoped<IProjectProcessingService, ProjectProcessingService>();
builder.Services.AddSingleton<IStepScriptRunner, StepScriptRunner>();

builder.Services.AddHttpClient<IEmbeddingService, GeminiEmbeddingService>();
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();
builder.Services.AddSingleton<IProjectHistoryIndex, QdrantProjectHistoryIndex>();
builder.Services.AddScoped<ICadRagRetriever, CadRagRetriever>();
builder.Services.AddScoped<ICadTemplateIndexer, CadTemplateIndexer>();
builder.Services.AddScoped<ICadTemplateMatcher, CadTemplateMatcher>();
builder.Services.AddScoped<ICadAssistConversationService, CadAssistConversationService>();
builder.Services.AddScoped<ILlmParameterExtractor, LlmParameterExtractor>();
builder.Services.AddSingleton<IDxfGeometryParser, DxfGeometryParser>();
builder.Services.AddSingleton<IBlueprintOcrService, BlueprintOcrService>();
builder.Services.AddScoped<IDrawingProcessingService, DrawingProcessingService>();
builder.Services.AddScoped<ICadLibraryService, CadLibraryService>();
var app = builder.Build();

{
    var manifestRegistry = app.Services.GetService<CadToCode.Core.Manifests.IEquipmentManifestRegistry>();

    if (manifestRegistry is not null)
    {
        app.Logger.LogInformation(
            "Equipment manifests loaded: {Ids}",
            string.Join(", ", manifestRegistry.All.Select(m => m.Id)));

        foreach (var issue in manifestRegistry.LoadIssues)
        {
            app.Logger.LogWarning(
                "Manifest issue {Code} at {Path}: {Message}",
                issue.Code,
                issue.Path,
                issue.Message);
        }
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();