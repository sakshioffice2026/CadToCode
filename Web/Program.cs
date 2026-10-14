using CadToCode.Business.Interfaces;
using CadToCode.Business.Sandbox;
using CadToCode.Business.Services;
using CadToCode.Database;
using CadToCode.LLM.Interfaces;
using CadToCode.LLM.Memory;
using CadToCode.LLM.Services;
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
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection(QdrantOptions.SectionName));
builder.Services.Configure<GeminiEmbeddingOptions>(builder.Configuration.GetSection(GeminiEmbeddingOptions.SectionName));

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
builder.Services.AddSingleton<ILocalAiService, LocalAiService>();
builder.Services.AddScoped<IAiComponentClassifier, AiComponentClassifier>();
builder.Services.AddScoped<ICodeGenerationService, CodeGenerationService>();
builder.Services.AddScoped<IDefinitionLearningService, DefinitionLearningService>();
builder.Services.AddScoped<IProjectProcessingService, ProjectProcessingService>();
builder.Services.AddSingleton<IStepScriptRunner, StepScriptRunner>();

builder.Services.AddHttpClient<IEmbeddingService, GeminiEmbeddingService>();
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();
builder.Services.AddScoped<ICadRagRetriever, CadRagRetriever>();
builder.Services.AddScoped<ICadLibraryService, CadLibraryService>();

var app = builder.Build();

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