using CadToCode.Models.Dtos;

namespace CadToCode.Business.Interfaces;

public interface IManufacturingDrawingService
{
    Task<CadAssistManufacturingResult> GenerateFromGaDxfAsync(
        Stream gaDxfStream,
        string gaFileName,
        CadAssistManufacturingRequest request,
        CancellationToken cancellationToken = default);

    Task<CadAssistManufacturingResult> GenerateFromJobAsync(
        CadAssistManufacturingRequest request,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string FileName)?> OpenDrawingAsync(
        int jobId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string FileName)?> OpenZipAsync(
        int jobId,
        CancellationToken cancellationToken = default);

    Task<CadAssistStepResult> GenerateStepFilesAsync(
        CadAssistStepRequest request,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string FileName)?> OpenStepAsync(
        int jobId,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string FileName)?> OpenStepZipAsync(
        int jobId,
        CancellationToken cancellationToken = default);

    Task<CadAssistStepResult> SaveTemplateStepAsync(
    int jobId,
    string fileName,
    string stepContent,
    string title,
    CancellationToken cancellationToken = default);
}