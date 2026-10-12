namespace CadToCode.Business.Interfaces;

public interface IDxfCompatibilityService
{
    Task<DxfCompatibilityResult> PrepareAsync(Stream input, CancellationToken cancellationToken = default);
}

public sealed class DxfCompatibilityResult : IDisposable
{
    public DxfCompatibilityResult(Stream stream)
    {
        Stream = stream;
    }

    public Stream Stream { get; }
    public string? OriginalVersionCode { get; set; }
    public string? OriginalVersionName { get; set; }
    public string? TargetVersionCode { get; set; }
    public bool HeaderPatched { get; set; }
    public bool IsBinaryDxf { get; set; }
    public bool IsDwg { get; set; }
    public List<string> Notes { get; } = new();

    public void Dispose()
    {
        Stream.Dispose();
    }
}