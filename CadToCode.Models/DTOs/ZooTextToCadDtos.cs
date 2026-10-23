namespace CadToCode.Models.Dtos;

public static class ZooJobStatus
{
    public const string Queued = "queued";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed class ZooGenerateRequestDto
{
    public string Prompt { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = string.Empty;
}

public sealed class ZooOutputFileDto
{
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class ZooJobDto
{
    public string Id { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = string.Empty;
    public string Status { get; set; } = ZooJobStatus.Queued;
    public string? Error { get; set; }
    public string? Kcl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ZooOutputFileDto> Files { get; set; } = new();
}

public sealed class ZooActionResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public ZooJobDto? Job { get; set; }

    public static ZooActionResultDto Ok(ZooJobDto job) =>
        new() { Success = true, Job = job };

    public static ZooActionResultDto Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

public sealed class ZooIndexViewModel
{
    public bool IsConfigured { get; set; }
    public IReadOnlyList<string> Formats { get; set; } = Array.Empty<string>();
    public string DefaultFormat { get; set; } = "step";
    public int MaxPromptChars { get; set; } = 2000;
    public IReadOnlyList<ZooJobDto> History { get; set; } = Array.Empty<ZooJobDto>();
}
