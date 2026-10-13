using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterFlow.Services;

internal sealed class OpenCodeService : IDisposable
{
    private const string ServerUsername = "opencode";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };
    private Process? _process;
    private Uri? _baseUri;
    private string? _workspace;

    public bool IsRunning => _process is { HasExited: false } && _baseUri is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        AppPaths.EnsureDirectories();
        var command = FindOpenCodeCommand();
        if (command is null)
        {
            await new OpenCodeInstaller().EnsureInstalledAsync(null, cancellationToken);
            command = FindOpenCodeCommand();
        }
        if (command is null)
            throw new FileNotFoundException("OpenCodeを自動で準備できませんでした。ネットワーク接続と空き容量を確認してください。");

        var port = GetAvailablePort();
        var serverPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _baseUri = new Uri($"http://127.0.0.1:{port}/");
        _workspace = Path.Combine(AppPaths.TemporaryDirectory, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);

        var info = CreateStartInfo(command, port, _workspace);
        info.Environment["OPENCODE_SERVER_USERNAME"] = ServerUsername;
        info.Environment["OPENCODE_SERVER_PASSWORD"] = serverPassword;
        // Better Flow only needs the model catalog already available to the local
        // OpenCode profile. A remote models.dev refresh is unrelated to provider
        // connection state and can stall catalog initialization on network or lock waits.
        info.Environment["OPENCODE_DISABLE_MODELS_FETCH"] = "1";
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += DiscardOutput;
        _process.ErrorDataReceived += DiscardOutput;
        var startupStage = "OpenCode serverの起動";
        var lastHealthError = "OpenCode serverから応答がありません。";
        try
        {
            ConfigureWorkspaceToolPermissions(_workspace);
            if (!_process.Start()) throw new InvalidOperationException("OpenCodeを起動できませんでした。");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            ConfigureHttpAuth(serverPassword);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            startupStage = "OpenCode serverへの接続";
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    throw new InvalidOperationException("AIモード用のOpenCode serverを起動できませんでした。OpenCodeのインストール状態を確認してください。");
                try
                {
                    using var response = await _http.GetAsync(new Uri(_baseUri, "global/health"), timeout.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("OpenCode serverのHTTP認証に失敗しました。AIモード用の認証設定を確認してください。");

                    if (response.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (document.RootElement.TryGetProperty("healthy", out var healthy) && healthy.GetBoolean())
                            return;
                        lastHealthError = "OpenCode serverのhealth応答がhealthyではありません。";
                    }
                    else lastHealthError = $"OpenCode serverのhealth応答はHTTP {(int)response.StatusCode}です。";
                }
                catch (HttpRequestException ex) { lastHealthError = ex.GetBaseException().Message; }
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = startupStage == "OpenCode serverへの接続" ? " " + lastHealthError : string.Empty;
            StopProcess();
            throw new TimeoutException($"{startupStage}が90秒以内に完了しませんでした。OpenCodeの起動状態とネットワーク設定を確認してください。{detail}");
        }
        catch
        {
            StopProcess();
            throw;
        }
    }

    public Task<string> RewriteAsync(string transcript, string providerId, string modelId,
        CancellationToken cancellationToken = default) =>
        GenerateTextAsync(providerId, modelId, TranscriptRewritePrompt.SystemMessage,
            TranscriptRewritePrompt.CreateUserMessage(transcript), cancellationToken);

    public Task<string> EditSelectedTextAsync(string selectedText, string instruction, string providerId, string modelId,
        CancellationToken cancellationToken = default) =>
        GenerateTextAsync(providerId, modelId, SelectionEditPrompt.SystemMessage,
            SelectionEditPrompt.CreateUserMessage(selectedText, instruction), cancellationToken);

    private async Task<string> GenerateTextAsync(string providerId, string modelId, string systemMessage,
        string userMessage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        string? sessionId = null;
        try
        {
            if (!IsRunning) throw new InvalidOperationException("AIモード用のOpenCodeが起動していません。AIモードを再度有効にしてください。");
            await EnsureModelAvailableAsync(providerId, modelId, cancellationToken);

            using var create = await _http.PostAsync(new Uri(_baseUri!, "session"),
        JsonBody(new { title = "Better Flow" }), cancellationToken);
            create.EnsureSuccessStatusCode();
            using var session = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
            sessionId = session.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("OpenCodeからセッションIDが返りませんでした。");

            var body = new
            {
                agent = "build",
                model = new { providerID = providerId, modelID = modelId },
                system = systemMessage,
                parts = new[] { new { type = "text", text = userMessage } }
            };
            using var message = await _http.PostAsync(
                new Uri(_baseUri!, $"session/{Uri.EscapeDataString(sessionId)}/message"), JsonBody(body), cancellationToken);
            var responseBody = await message.Content.ReadAsStringAsync(cancellationToken);
            if (!message.IsSuccessStatusCode)
                throw new HttpRequestException(BuildProviderError(message.StatusCode, responseBody), null, message.StatusCode);

            using var response = JsonDocument.Parse(responseBody);
            var responseError = ExtractResponseError(response.RootElement);
            if (!string.IsNullOrWhiteSpace(responseError))
                throw new InvalidDataException(BuildResponseError(responseError));

            var texts = new List<string>();
            if (response.RootElement.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                        part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        texts.Add(text.GetString() ?? string.Empty);
                }
            }

            var result = string.Join("\n", texts).Trim();
            if (!TranscriptFormatter.HasMeaningfulText(result))
                throw new InvalidDataException("モデルから文字として使える回答が返りませんでした。");
            return result;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && IsRunning)
            {
                try
                {
                    using var delete = await _http.DeleteAsync(
                        new Uri(_baseUri!, $"session/{Uri.EscapeDataString(sessionId)}"), cancellationToken);
                }
                catch { }
            }
            _gate.Release();
        }
    }

    public void Stop() => StopProcess();

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
        _gate.Dispose();
    }

    public async Task<IReadOnlyList<OpenCodeModelOption>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) throw new InvalidOperationException("OpenCode serverが起動していません。");
        using var response = await _http.GetAsync(new Uri(_baseUri!, "provider"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("connected", out var connectedProvidersElement) ||
            connectedProvidersElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenCodeから接続中providerの一覧が返りませんでした。安全のため、全モデル一覧は表示しません。OpenCodeを更新して再試行してください。");

        var connectedProviderIds = connectedProvidersElement.EnumerateArray()
            .Where(providerId => providerId.ValueKind == JsonValueKind.String)
            .Select(providerId => providerId.GetString())
            .Where(providerId => !string.IsNullOrWhiteSpace(providerId))
            .Select(providerId => providerId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var providers = root.TryGetProperty("all", out var allProviders) && allProviders.ValueKind == JsonValueKind.Array
            ? allProviders
            : root.TryGetProperty("providers", out var currentProviders) && currentProviders.ValueKind == JsonValueKind.Array
                ? currentProviders
                : default;
        if (providers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("OpenCodeからモデル一覧が返りませんでした。OpenCodeを更新して再試行してください。");

        var options = new List<OpenCodeModelOption>();
        var uniqueModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers.EnumerateArray())
        {
            if (!provider.TryGetProperty("id", out var providerIdElement) || providerIdElement.ValueKind != JsonValueKind.String)
                continue;
            var providerId = providerIdElement.GetString();
            if (string.IsNullOrWhiteSpace(providerId) || !connectedProviderIds.Contains(providerId) ||
                !provider.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Object)
                continue;

            var providerName = provider.TryGetProperty("name", out var providerNameElement) &&
                               providerNameElement.ValueKind == JsonValueKind.String &&
                               !string.IsNullOrWhiteSpace(providerNameElement.GetString())
                ? providerNameElement.GetString()!
                : providerId;

            foreach (var model in models.EnumerateObject())
            {
                var modelId = model.Value.TryGetProperty("id", out var modelIdElement) && modelIdElement.ValueKind == JsonValueKind.String
                    ? modelIdElement.GetString()
                    : model.Name;
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                var actualProviderId = model.Value.TryGetProperty("providerID", out var modelProviderElement) &&
                                       modelProviderElement.ValueKind == JsonValueKind.String &&
                                       !string.IsNullOrWhiteSpace(modelProviderElement.GetString())
                    ? modelProviderElement.GetString()!
                    : providerId;
                if (!connectedProviderIds.Contains(actualProviderId)) continue;
                if (!uniqueModels.Add($"{actualProviderId}/{modelId}")) continue;
                options.Add(new OpenCodeModelOption(actualProviderId, providerName, modelId,
                    GetModelDisplayName(modelId, model.Value)));
            }
        }

        return options.OrderByDescending(option => option.ProviderId == "opencode" &&
                                                    option.ModelId == "muse-spark-1.3-contributor-free")
            .ThenBy(option => option.ProviderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task EnsureModelAvailableAsync(string providerId, string modelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("OpenCodeのモデル一覧からモデルを選択してください。");
        var available = await GetModelsAsync(cancellationToken);
        if (!available.Any(option => string.Equals(option.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(option.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("選択中のモデルはOpenCodeで接続されていません。OpenCode Desktopで接続してから一覧を更新してください。");
    }

    private static string GetModelDisplayName(string modelId, JsonElement model) =>
        model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
            ? $"{name.GetString()} ({modelId})"
            : modelId;

    public static bool IsCommandAvailable() => FindOpenCodeCommand() is not null;

    private static OpenCodeCommand? FindOpenCodeCommand()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        OpenCodeCommand? commandScript = null;
        foreach (var directory in directories)
        {
            var shim = Path.Combine(directory, "opencode.cmd");
            if (!File.Exists(shim)) continue;
            var target = ResolveNpmShimTarget(shim);
            if (target is not null) return new OpenCodeCommand(target, OpenCodeLaunchKind.Executable);
            commandScript ??= new OpenCodeCommand(shim, OpenCodeLaunchKind.CommandScript);
        }

        foreach (var directory in directories)
        {
            var executable = Path.Combine(directory, "opencode.exe");
            if (File.Exists(executable))
                return new OpenCodeCommand(executable, OpenCodeLaunchKind.Executable);
        }

        if (commandScript is not null) return commandScript;

        foreach (var directory in directories)
        {
            var script = Path.Combine(directory, "opencode.ps1");
            if (File.Exists(script))
                return new OpenCodeCommand(script, OpenCodeLaunchKind.PowerShellScript);
        }

        if (OpenCodeInstaller.IsUsableExecutable(AppPaths.OpenCodeExecutable))
            return new OpenCodeCommand(AppPaths.OpenCodeExecutable, OpenCodeLaunchKind.Executable);

        return null;
    }

    private static string? ResolveNpmShimTarget(string shimPath)
    {
        try
        {
            var script = File.ReadAllText(shimPath);
            var match = Regex.Match(script,
                "\\\"%dp0%[\\\\/](?<relative>node_modules[\\\\/]opencode-ai[\\\\/]bin[\\\\/]opencode\\.exe)\\\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            var relative = match.Groups["relative"].Value.Replace('\\', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shimPath)!, relative));
            return File.Exists(target) ? target : null;
        }
        catch { return null; }
    }

    private static ProcessStartInfo CreateStartInfo(OpenCodeCommand command, int port, string workspace)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var arguments = $"serve --hostname 127.0.0.1 --port {port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        switch (command.Kind)
        {
            case OpenCodeLaunchKind.Executable:
                info.FileName = command.Path;
                foreach (var argument in new[] { "serve", "--hostname", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                    info.ArgumentList.Add(argument);
                break;
            case OpenCodeLaunchKind.CommandScript:
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                info.ArgumentList.Add("/d");
                info.ArgumentList.Add("/s");
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add($"call \"{command.Path}\" {arguments}");
                break;
            case OpenCodeLaunchKind.PowerShellScript:
                info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", command.Path,
                             "serve", "--hostname", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                    info.ArgumentList.Add(argument);
                break;
        }
        return info;
    }

    private void ConfigureHttpAuth(string password)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(ServerUsername + ":" + password)));
    }

    private static void ConfigureWorkspaceToolPermissions(string workspace)
    {
        const string safeContextFile = "better-flow-context.txt";
        var safeContextAbsolutePath = Path.GetFullPath(Path.Combine(workspace, safeContextFile))
            .Replace(Path.DirectorySeparatorChar, '/');
        // Zen free-tier requests can be rejected when read/bash are denied. Keep only
        // one inert workspace file readable and a no-op echo command executable.
        var permissions = new Dictionary<string, object>
        {
            ["*"] = "deny",
            ["read"] = new Dictionary<string, string>
            {
                ["*"] = "deny",
                [safeContextFile] = "allow",
                [safeContextAbsolutePath] = "allow"
            },
            ["bash"] = new Dictionary<string, string>
            {
                ["*"] = "deny",
                ["echo *"] = "allow"
            }
        };
        var config = new Dictionary<string, object> { ["permission"] = permissions };
        File.WriteAllText(Path.Combine(workspace, "opencode.json"), JsonSerializer.Serialize(config), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(workspace, safeContextFile), "Better Flow temporary formatter workspace.", new UTF8Encoding(false));
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string BuildProviderError(HttpStatusCode statusCode, string body)
    {
        var safeDetail = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
                safeDetail = ExtractErrorDetail(error) ?? "";
        }
        catch { }
        if (statusCode == HttpStatusCode.Forbidden && !string.IsNullOrWhiteSpace(safeDetail))
            return BuildResponseError(safeDetail);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "OpenCode Desktopのプロファイルで選択モデルの認証と接続状態を確認してください。",
            HttpStatusCode.NotFound => "OpenCode Desktopのプロファイルに選択モデルが見つかりません。モデル一覧を更新してください。",
            HttpStatusCode.TooManyRequests => "選択したOpenCodeモデルの利用上限に達したようです。時間をおいて再試行してください。",
            _ => $"OpenCodeのモデル要求に失敗しました ({(int)statusCode})。 {safeDetail}".Trim()
        };
    }

    private static string? ExtractResponseError(JsonElement response)
    {
        if (response.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("error", out var infoError))
            return ExtractErrorDetail(infoError);

        return response.TryGetProperty("error", out var error)
            ? ExtractErrorDetail(error)
            : null;
    }

    private static string? ExtractErrorDetail(JsonElement error, int depth = 0)
    {
        if (depth > 6) return null;
        if (error.ValueKind == JsonValueKind.String)
            return LimitErrorDetail(error.GetString());
        if (error.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "message", "detail", "reason", "error_description" })
        {
            if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return LimitErrorDetail(value.GetString());
        }

        foreach (var name in new[] { "error", "data", "cause", "details" })
        {
            if (error.TryGetProperty(name, out var nested))
            {
                var detail = ExtractErrorDetail(nested, depth + 1);
                if (!string.IsNullOrWhiteSpace(detail)) return detail;
            }
        }

        foreach (var name in new[] { "name", "type" })
        {
            if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return LimitErrorDetail(value.GetString());
        }

        return null;
    }

    private static string BuildResponseError(string detail)
    {
        if (detail.Contains("Token refresh failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "OpenCodeのOAuth認証を更新できませんでした。OpenCode Desktopで選択モデルのproviderに再ログインし、Better FlowのAIモードを一度オフにしてからオンにしてください。詳細: " + detail;

        if (detail.Contains("can only be used from within OpenCode", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("FreeTierError", StringComparison.OrdinalIgnoreCase))
            return "OpenCode Zenの無料枠がこの要求を拒否しました。OpenCodeのツール設定または無料枠の判定を確認してください。詳細: " + detail;

        return "OpenCodeからモデル応答エラーが返りました: " + detail;
    }

    private static string LimitErrorDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        var normalized = Regex.Replace(detail, @"\s+", " ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DiscardOutput(object sender, DataReceivedEventArgs args) { }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _baseUri = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch { }
            process.Dispose();
        }
        var workspace = _workspace;
        _workspace = null;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    private sealed record OpenCodeCommand(string Path, OpenCodeLaunchKind Kind);
    private enum OpenCodeLaunchKind { Executable, CommandScript, PowerShellScript }
}
