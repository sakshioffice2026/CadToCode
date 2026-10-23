using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using CadToCode.Repositories.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public sealed class ZooTextToCadService : IZooTextToCadService
{
    private readonly HttpClient _http;
    private readonly IZooGenerationRepository _repository;
    private readonly ZooOptions _options;
    private readonly ILogger<ZooTextToCadService> _logger;

    public ZooTextToCadService(
        HttpClient http,
        IZooGenerationRepository repository,
        IOptions<ZooOptions> options,
        ILogger<ZooTextToCadService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 10, 300));
    }

    public bool IsConfigured =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !_options.ApiKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);

    public async Task<ZooActionResultDto> SubmitAsync(
        ZooGenerateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
        {
            return ZooActionResultDto.Fail("Zoo is not configured. Set ZooOptions:ApiKey and restart the app.");
        }

        var prompt = (request.Prompt ?? string.Empty).Trim();

        if (prompt.Length == 0)
        {
            return ZooActionResultDto.Fail("Describe the part you want to generate.");
        }

        if (prompt.Length > _options.MaxPromptChars)
        {
            return ZooActionResultDto.Fail($"The prompt is limited to {_options.MaxPromptChars} characters.");
        }

        var format = (string.IsNullOrWhiteSpace(request.OutputFormat)
                ? _options.DefaultOutputFormat
                : request.OutputFormat).Trim().ToLowerInvariant();

        if (!_options.OutputFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
        {
            return ZooActionResultDto.Fail("That output format is not available.");
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/ai/text-to-cad/{Uri.EscapeDataString(format)}?kcl=true";

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { prompt }),
                Encoding.UTF8,
                "application/json")
        };

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Zoo text-to-CAD submit failed with status {Status}.", (int)response.StatusCode);
                return ZooActionResultDto.Fail(DescribeFailure(response.StatusCode, body));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!Guid.TryParse(ReadString(root, "id"), out var guid))
            {
                return ZooActionResultDto.Fail("Zoo returned an unexpected response.");
            }

            var job = new ZooJobDto
            {
                Id = guid.ToString("D"),
                Prompt = prompt,
                OutputFormat = format,
                Status = NormalizeStatus(ReadString(root, "status")),
                CreatedAt = DateTime.UtcNow
            };

            await _repository.AddAsync(job, cancellationToken);
            return ZooActionResultDto.Ok(job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ZooActionResultDto.Fail("Zoo did not respond in time. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Zoo text-to-CAD submit could not reach Zoo.");
            return ZooActionResultDto.Fail("Zoo could not be reached. Check the connection and try again.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Zoo text-to-CAD submit returned invalid JSON.");
            return ZooActionResultDto.Fail("Zoo returned an unexpected response.");
        }
    }

    public async Task<ZooActionResultDto> GetStatusAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return ZooActionResultDto.Fail("Unknown request.");
        }

        var job = await _repository.GetAsync(guid, cancellationToken);

        if (job is null)
        {
            return ZooActionResultDto.Fail("Unknown request.");
        }

        if (job.Status == ZooJobStatus.Completed || job.Status == ZooJobStatus.Failed)
        {
            return ZooActionResultDto.Ok(job);
        }

        if (!IsConfigured)
        {
            return ZooActionResultDto.Fail("Zoo is not configured. Set ZooOptions:ApiKey and restart the app.");
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/user/text-to-cad/{job.Id}";

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Zoo text-to-CAD status failed with status {Status}.", (int)response.StatusCode);
                return ZooActionResultDto.Fail(DescribeFailure(response.StatusCode, body));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var previousStatus = job.Status;
            job.Status = NormalizeStatus(ReadString(root, "status"));

            if (job.Status == ZooJobStatus.Failed)
            {
                var error = ReadString(root, "error");
                job.Error = string.IsNullOrWhiteSpace(error)
                    ? "Zoo could not generate this model."
                    : Truncate(error, 400);

                await _repository.UpdateAsync(job, null, cancellationToken);
            }
            else if (job.Status == ZooJobStatus.Completed)
            {
                job.Kcl = ReadString(root, "code");

                var files = ReadOutputs(root, job.OutputFormat);

                if (files.Count == 0)
                {
                    job.Status = ZooJobStatus.Failed;
                    job.Error = "Zoo finished without returning a model file.";
                    await _repository.UpdateAsync(job, null, cancellationToken);
                }
                else
                {
                    job.Files = files
                        .Select(f => new ZooOutputFileDto { Name = f.Name, SizeBytes = f.Content.LongLength })
                        .ToList();

                    await _repository.UpdateAsync(job, files, cancellationToken);
                }
            }
            else if (job.Status != previousStatus)
            {
                await _repository.UpdateAsync(job, null, cancellationToken);
            }

            return ZooActionResultDto.Ok(job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ZooActionResultDto.Fail("Zoo did not respond in time. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Zoo text-to-CAD status could not reach Zoo.");
            return ZooActionResultDto.Fail("Zoo could not be reached. Check the connection and try again.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Zoo text-to-CAD status returned invalid JSON.");
            return ZooActionResultDto.Fail("Zoo returned an unexpected response.");
        }
    }

    public Task<IReadOnlyList<ZooJobDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.ListRecentAsync(_options.MaxHistory, cancellationToken);
    }

    public async Task<(byte[] Content, string FileName)?> GetFileAsync(
        string id,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return await _repository.GetFileAsync(guid, fileName, cancellationToken);
    }

    private static List<ZooFileContent> ReadOutputs(JsonElement root, string outputFormat)
    {
        var files = new List<ZooFileContent>();

        if (!root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
        {
            return files;
        }

        foreach (var property in outputs.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = SafeFileName(property.Name, outputFormat);

            if (name is null || files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(property.Value.GetString() ?? string.Empty);
            }
            catch (FormatException)
            {
                continue;
            }

            files.Add(new ZooFileContent { Name = name, Content = bytes });
        }

        return files;
    }

    private static string? SafeFileName(string raw, string outputFormat)
    {
        var name = Path.GetFileName(raw ?? string.Empty);

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            return null;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (name.Length > 200)
        {
            name = name[..200];
        }

        if (string.IsNullOrEmpty(Path.GetExtension(name)))
        {
            name += "." + outputFormat;
        }

        return name;
    }

    private static string NormalizeStatus(string? status)
    {
        return (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "completed" => ZooJobStatus.Completed,
            "failed" => ZooJobStatus.Failed,
            "in_progress" => ZooJobStatus.InProgress,
            _ => ZooJobStatus.Queued
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string DescribeFailure(HttpStatusCode status, string body)
    {
        var detail = ExtractMessage(body);

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Zoo rejected the API token.",
            HttpStatusCode.TooManyRequests => "Zoo rate limit reached. Try again shortly.",
            HttpStatusCode.BadRequest => string.IsNullOrWhiteSpace(detail)
                ? "Zoo could not use that prompt."
                : detail,
            _ => $"Zoo returned an error ({(int)status})."
        };
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var message = ReadString(document.RootElement, "message");
            return string.IsNullOrWhiteSpace(message) ? null : Truncate(message, 300);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}