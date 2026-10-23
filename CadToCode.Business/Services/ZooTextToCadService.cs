using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CadToCode.Business.Interfaces;
using CadToCode.Models.Dtos;
using CadToCode.Models.Options;
using CadToCode.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CadToCode.Business.Services;

public sealed class ZooTextToCadService : IZooTextToCadService
{
    private static readonly string[] WebSocketPaths =
    {
        "/ws/ml/zookeeper",
        "/ws/ml/copilot"
    };

    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IZooGenerationRepository _repository;
    private readonly ZooOptions _options;
    private readonly ILogger<ZooTextToCadService> _logger;

    public ZooTextToCadService(
        HttpClient http,
        IServiceScopeFactory scopeFactory,
        IZooGenerationRepository repository,
        IOptions<ZooOptions> options,
        ILogger<ZooTextToCadService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var guid = Guid.NewGuid();

        var job = new ZooJobDto
        {
            Id = guid.ToString("D"),
            Prompt = prompt,
            OutputFormat = format,
            Status = ZooJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _repository.AddAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zoo text-to-CAD job could not be saved.");
            return ZooActionResultDto.Fail("The request could not be saved. Please try again.");
        }

        _ = Task.Run(() => RunJobAsync(guid, prompt, format));

        return ZooActionResultDto.Ok(job);
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

        var running = job.Status == ZooJobStatus.Queued || job.Status == ZooJobStatus.InProgress;

        if (running && DateTime.UtcNow - job.CreatedAt > StaleAfter)
        {
            job.Status = ZooJobStatus.Failed;
            job.Error = "Zoo generation timed out. Please try again.";
            await _repository.UpdateAsync(job, null, cancellationToken);
        }

        return ZooActionResultDto.Ok(job);
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

    private async Task RunJobAsync(Guid id, string prompt, string format)
    {
        using var timeout = new CancellationTokenSource(JobTimeout);
        var token = timeout.Token;

        try
        {
            await SaveJobAsync(id, ZooJobStatus.InProgress, null, null, null);

            var kclFiles = await GenerateKclAsync(prompt, token);
            var mainKcl = PrepareMainKcl(kclFiles);

            await SaveJobAsync(id, ZooJobStatus.InProgress, null, mainKcl, null);

            var files = await ExportAsync(kclFiles, format, token);

            files.Add(new ZooFileContent
            {
                Name = "model.kcl",
                Content = new UTF8Encoding(false).GetBytes(mainKcl)
            });

            await SaveJobAsync(id, ZooJobStatus.Completed, null, mainKcl, files);
        }
        catch (ZooJobException ex)
        {
            _logger.LogWarning("Zoo job {Id} failed: {Message}", id, ex.Message);
            await TrySaveFailureAsync(id, ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Zoo job {Id} timed out.", id);
            await TrySaveFailureAsync(id, "Zoo did not respond in time. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zoo job {Id} failed unexpectedly.", id);
            await TrySaveFailureAsync(id, "Zoo could not generate this model.");
        }
    }

    private async Task TrySaveFailureAsync(Guid id, string message)
    {
        try
        {
            await SaveJobAsync(id, ZooJobStatus.Failed, message, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zoo job {Id} failure could not be saved.", id);
        }
    }

    private async Task SaveJobAsync(
        Guid id,
        string status,
        string? error,
        string? kcl,
        IReadOnlyList<ZooFileContent>? files)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IZooGenerationRepository>();

        var job = await repository.GetAsync(id, CancellationToken.None);

        if (job is null)
        {
            return;
        }

        job.Status = status;
        job.Error = error is null ? null : Truncate(error, 400);

        if (kcl is not null)
        {
            job.Kcl = kcl;
        }

        await repository.UpdateAsync(job, files, CancellationToken.None);
    }

    private async Task<Dictionary<string, string>> GenerateKclAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');

        if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "wss://" + baseUrl.Substring("https://".Length);
        }
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "ws://" + baseUrl.Substring("http://".Length);
        }

        var apiKey = _options.ApiKey.Trim();

        foreach (var path in WebSocketPaths)
        {
            var uri = new Uri(baseUrl + path);

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning("Zoo WebSocket {Path} could not connect: {Message}", path, ex.Message);
                continue;
            }

            _logger.LogInformation("Zoo WebSocket connected on {Path}.", path);

            return await ConversationAsync(socket, prompt, apiKey, cancellationToken);
        }

        throw new ZooJobException("Zoo could not be reached. Check the connection and API token, then try again.");
    }

    private async Task<Dictionary<string, string>> ConversationAsync(
        ClientWebSocket socket,
        string prompt,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var state = new ConversationState();

        await SendTextAsync(
            socket,
            JsonSerializer.Serialize(new
            {
                type = "headers",
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey }
            }),
            cancellationToken);

        await SendTextAsync(
            socket,
            JsonSerializer.Serialize(new { type = "user", content = prompt }),
            cancellationToken);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pingTask = PingLoopAsync(socket, pingCts.Token);

        try
        {
            while (true)
            {
                string? text;

                try
                {
                    text = await ReceiveTextAsync(socket, cancellationToken);
                }
                catch (WebSocketException ex)
                {
                    if (state.Files.Count > 0)
                    {
                        _logger.LogWarning("Zoo WebSocket closed early after KCL was received: {Message}", ex.Message);
                        break;
                    }

                    throw new ZooJobException("Zoo closed the connection before returning a model.");
                }

                if (text is null)
                {
                    break;
                }

                JsonDocument document;

                try
                {
                    document = JsonDocument.Parse(text);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    if (HandleMessage(document.RootElement, state))
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            pingCts.Cancel();

            try
            {
                await pingTask;
            }
            catch (Exception)
            {
            }

            await CloseQuietlyAsync(socket);
        }

        if (state.Files.Count == 0)
        {
            throw new ZooJobException(
                string.IsNullOrWhiteSpace(state.LastToolError)
                    ? "Zoo did not return a model for this prompt. Try a more specific description."
                    : Truncate(state.LastToolError!, 300));
        }

        return state.Files;
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(PingInterval, cancellationToken);
                await SendTextAsync(socket, "{\"type\":\"ping\"}", cancellationToken);
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
        }
        catch (Exception)
        {
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private bool HandleMessage(JsonElement root, ConversationState state)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "end_of_stream":
                    return true;

                case "error":
                    {
                        var detail = ReadString(property.Value, "detail");

                        throw new ZooJobException(
                            string.IsNullOrWhiteSpace(detail)
                                ? "Zoo returned an error."
                                : "Zoo returned an error: " + Truncate(detail, 300));
                    }

                case "access_denied":
                    {
                        var code = ReadString(property.Value, "code");
                        var detail = ReadString(property.Value, "detail");

                        throw new ZooJobException(
                            "Zoo denied access" +
                            (string.IsNullOrWhiteSpace(code) ? "." : $" ({code}).") +
                            (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + Truncate(detail, 200)));
                    }

                case "tool_output":
                    CollectToolOutput(property.Value, state);
                    break;

                case "reasoning":
                    CollectReasoning(property.Value, state);
                    break;

                case "project_updated":
                    CollectTextFiles(property.Value, "files", state);
                    break;

                case "project_revision_updated":
                    CollectByteFiles(property.Value, "files", state);
                    break;

                default:
                    _logger.LogDebug("Zoo message {Name} ignored.", property.Name);
                    break;
            }
        }

        return false;
    }

    private static void CollectToolOutput(JsonElement value, ConversationState state)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var error = ReadString(result, "error");

        if (!string.IsNullOrWhiteSpace(error))
        {
            state.LastToolError = error;
        }

        if (result.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var output in outputs.EnumerateObject())
            {
                if (output.Value.ValueKind == JsonValueKind.String)
                {
                    AddFile(state, output.Name, output.Value.GetString());
                }
            }
        }

        if (result.TryGetProperty("zookeeper_edit_patch", out var patch) &&
            patch.ValueKind == JsonValueKind.Object &&
            patch.TryGetProperty("changed_files", out var changed) &&
            changed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in changed.EnumerateArray())
            {
                var status = ReadString(item, "status");
                var path = ReadString(item, "path");

                if (string.Equals(status, "created", StringComparison.OrdinalIgnoreCase))
                {
                    AddFile(state, path, ReadString(item, "contents"));
                }
                else if (string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveFile(state, path);
                }
            }
        }
    }

    private static void CollectReasoning(JsonElement value, ConversationState state)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (ReadString(value, "type"))
        {
            case "created_kcl_file":
            case "updated_kcl_file":
            case "created_project_file":
            case "updated_project_file":
                AddFile(state, ReadString(value, "file_name"), ReadString(value, "content"));
                break;

            case "deleted_kcl_file":
            case "deleted_project_file":
                RemoveFile(state, ReadString(value, "file_name"));
                break;

            case "generated_kcl_code":
                AddFile(state, "main.kcl", ReadString(value, "code"));
                break;
        }
    }

    private static void CollectTextFiles(JsonElement value, string propertyName, ConversationState state)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var files) ||
            files.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var file in files.EnumerateObject())
        {
            if (file.Value.ValueKind == JsonValueKind.String)
            {
                AddFile(state, file.Name, file.Value.GetString());
            }
        }
    }

    private static void CollectByteFiles(JsonElement value, string propertyName, ConversationState state)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var files) ||
            files.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var file in files.EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var bytes = new List<byte>();

            foreach (var item in file.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetByte(out var b))
                {
                    bytes.Add(b);
                }
            }

            AddFile(state, file.Name, Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }

    private static void AddFile(ConversationState state, string? rawPath, string? content)
    {
        var path = NormalizeKclPath(rawPath);

        if (path is null || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        state.Files[path] = DecodeIfBase64(content);
    }

    private static void RemoveFile(ConversationState state, string? rawPath)
    {
        var path = NormalizeKclPath(rawPath);

        if (path is not null)
        {
            state.Files.Remove(path);
        }
    }

    private static string? NormalizeKclPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var path = raw.Replace('\\', '/').Trim();

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path.Substring(2);
        }

        if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':'))
        {
            return null;
        }

        if (path.Split('/').Any(segment => segment.Length == 0 || segment == ".."))
        {
            return null;
        }

        return path.EndsWith(".kcl", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string DecodeIfBase64(string value)
    {
        if (value.Length < 16 || value.Length % 4 != 0 || value.Any(char.IsWhiteSpace))
        {
            return value;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(value));

            if (text.Contains('\n') || text.Contains('='))
            {
                return text;
            }
        }
        catch (FormatException)
        {
        }

        return value;
    }

    private static string PrepareMainKcl(Dictionary<string, string> files)
    {
        if (files.TryGetValue("main.kcl", out var main))
        {
            return main;
        }

        if (files.Count == 1)
        {
            var only = files.First();
            files.Remove(only.Key);
            files["main.kcl"] = only.Value;
            return only.Value;
        }

        throw new ZooJobException("Zoo did not return a main.kcl file.");
    }

    private async Task<List<ZooFileContent>> ExportAsync(
        Dictionary<string, string> kclFiles,
        string format,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "cadtocode-zoo-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");

        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);

        try
        {
            var inputRoot = Path.GetFullPath(input) + Path.DirectorySeparatorChar;

            foreach (var file in kclFiles)
            {
                var fullPath = Path.GetFullPath(Path.Combine(input, file.Key));

                if (!fullPath.StartsWith(inputRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, file.Value, new UTF8Encoding(false), cancellationToken);
            }

            await RunCliAsync(input, output, format, root, cancellationToken);

            var files = new List<ZooFileContent>();

            foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var name = SafeFileName(Path.GetFileName(path), format);

                if (name is null || files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                files.Add(new ZooFileContent
                {
                    Name = name,
                    Content = await File.ReadAllBytesAsync(path, cancellationToken)
                });
            }

            if (files.Count == 0)
            {
                throw new ZooJobException("Zoo finished without returning a model file.");
            }

            return files;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task RunCliAsync(
        string input,
        string output,
        string format,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var cli = Environment.GetEnvironmentVariable("ZOO_CLI_PATH");

        if (string.IsNullOrWhiteSpace(cli))
        {
            cli = "zoo";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = cli,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("kcl");
        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add(format);
        startInfo.ArgumentList.Add(input);
        startInfo.ArgumentList.Add(output);

        var token = _options.ApiKey.Trim();
        startInfo.Environment["ZOO_API_TOKEN"] = token;
        startInfo.Environment["KITTYCAD_API_TOKEN"] = token;

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Zoo CLI could not be started.");
            throw new ZooJobException("Zoo CLI was not found on the server. Install it and add it to PATH, or set ZOO_CLI_PATH.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
            }

            throw;
        }

        var outText = await standardOutput;
        var errText = await standardError;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(errText) ? outText : errText;
            _logger.LogWarning("Zoo CLI export failed with exit code {Code}: {Detail}", process.ExitCode, Truncate(detail, 500));

            throw new ZooJobException(
                "Zoo could not export the model: " + Truncate(detail.Trim(), 250));
        }
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

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed class ConversationState
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LastToolError { get; set; }
    }

    private sealed class ZooJobException : Exception
    {
        public ZooJobException(string message) : base(message)
        {
        }
    }
}