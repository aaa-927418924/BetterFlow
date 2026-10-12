using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AutoPrompt.Services;

internal sealed class OpenCodeService : IDisposable
{
    private const string ModelId = "muse-spark-1.3-contributor-free";
    private const string AgentId = "autoprompt";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private Process? _process;
    private Uri? _baseUri;
    private string? _sessionPassword;
    private string? _workspace;

    public bool IsRunning => _process is { HasExited: false } && _baseUri is not null;

    public async Task StartAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenCode Zen APIキーを入力してください。");
        if (IsRunning) return;

        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.OpenCodeExecutable))
            throw new FileNotFoundException("OpenCodeが見つかりません。セットアップをもう一度実行してください。", AppPaths.OpenCodeExecutable);

        var port = GetAvailablePort();
        _sessionPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _baseUri = new Uri($"http://127.0.0.1:{port}/");
        _workspace = Path.Combine(AppPaths.TemporaryDirectory, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);

        var info = new ProcessStartInfo
        {
            FileName = AppPaths.OpenCodeExecutable,
            WorkingDirectory = _workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("serve");
        info.ArgumentList.Add("--hostname");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var privateHome = Path.Combine(AppPaths.OpenCodeHome, "profile");
        var configHome = Path.Combine(AppPaths.OpenCodeHome, "config");
        var cacheHome = Path.Combine(AppPaths.OpenCodeHome, "cache");
        var dataHome = AppPaths.OpenCodeData;
        Directory.CreateDirectory(privateHome);
        Directory.CreateDirectory(configHome);
        Directory.CreateDirectory(cacheHome);
        Directory.CreateDirectory(dataHome);

        info.Environment["USERPROFILE"] = privateHome;
        info.Environment["HOME"] = privateHome;
        info.Environment["APPDATA"] = Path.Combine(privateHome, "AppData", "Roaming");
        info.Environment["LOCALAPPDATA"] = Path.Combine(privateHome, "AppData", "Local");
        info.Environment["XDG_CONFIG_HOME"] = configHome;
        info.Environment["XDG_DATA_HOME"] = dataHome;
        info.Environment["XDG_CACHE_HOME"] = cacheHome;
        info.Environment["AUTOPROMPT_ZEN_API_KEY"] = apiKey;
        info.Environment["OPENCODE_SERVER_PASSWORD"] = _sessionPassword;
        info.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        info.Environment["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
        info.Environment["OPENCODE_CONFIG_CONTENT"] = BuildConfiguration();

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += DiscardOutput;
        _process.ErrorDataReceived += DiscardOutput;
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("OpenCodeを起動できませんでした。");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            ConfigureHttpAuth(_sessionPassword);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            while (!timeout.IsCancellationRequested)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException("OpenCodeが起動直後に終了しました。アプリを再起動してもう一度お試しください。");
                try
                {
                    using var response = await _http.GetAsync(new Uri(_baseUri, "global/health"), timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (document.RootElement.TryGetProperty("healthy", out var healthy) && healthy.GetBoolean()) return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StopProcess();
            throw new TimeoutException("OpenCodeの起動を確認できませんでした。通信設定を確認して再試行してください。");
        }
        catch
        {
            StopProcess();
            throw;
        }
    }

    public async Task<string> RewriteAsync(string transcript, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? sessionId = null;
        try
        {
            if (!IsRunning) throw new InvalidOperationException("OpenCodeは起動していません。APIキーを登録し直してください。");

            using var create = await _http.PostAsync(new Uri(_baseUri!, "session"),
                JsonBody(new { title = "Auto Prompt" }), cancellationToken);
            create.EnsureSuccessStatusCode();
            using var session = JsonDocument.Parse(await create.Content.ReadAsStringAsync(cancellationToken));
            sessionId = session.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(sessionId)) throw new InvalidDataException("OpenCodeからセッションIDが返りませんでした。");

            var prompt = "次の日本語の文字起こしを、元の意図・対象・条件を保った短く明確な指示文に整えてください。\n" +
                         "出力は日本語、最大10行、前置きや解説なし。必要なら箇条書きを使い、曖昧な事実を勝手に補わないでください。\n\n" + transcript;
            using var message = await _http.PostAsync(new Uri(_baseUri!, $"session/{Uri.EscapeDataString(sessionId)}/message"),
                JsonBody(new { agent = AgentId, parts = new[] { new { type = "text", text = prompt } } }), cancellationToken);
            var body = await message.Content.ReadAsStringAsync(cancellationToken);
            if (!message.IsSuccessStatusCode)
                throw new HttpRequestException(BuildProviderError(message.StatusCode, body), null, message.StatusCode);

            using var response = JsonDocument.Parse(body);
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
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidDataException("モデルから空の回答が返りました。");
            return result;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && IsRunning)
            {
                try
                {
                    using var delete = await _http.DeleteAsync(new Uri(_baseUri!, $"session/{Uri.EscapeDataString(sessionId)}"), cancellationToken);
                }
                catch { }
            }
            _gate.Release();
        }
    }

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
        _gate.Dispose();
    }

    private static string BuildConfiguration() => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"] = $"zen/{ModelId}",
            ["small_model"] = $"zen/{ModelId}",
            ["plugin"] = Array.Empty<string>(),
            ["mcp"] = new { },
            ["tools"] = new Dictionary<string, bool> { ["*"] = false },
            ["provider"] = new Dictionary<string, object>
            {
                ["zen"] = new
                {
                    npm = "@ai-sdk/openai",
                    name = "OpenCode Zen",
                    options = new { baseURL = "https://opencode.ai/zen/v1", apiKey = "{env:AUTOPROMPT_ZEN_API_KEY}" },
                    models = new Dictionary<string, object>
                    {
                        [ModelId] = new
                        {
                            name = "Muse Spark 1.3 Contributor Free",
                            limit = new { context = 128_000, output = 4_096 }
                        }
                    }
                }
            },
            ["agent"] = new Dictionary<string, object>
            {
                [AgentId] = new
                {
                    mode = "primary",
                    model = $"zen/{ModelId}",
                    permission = new Dictionary<string, string> { ["*"] = "deny" },
                    prompt = "You only rewrite the user's Japanese speech transcript into a concise Japanese prompt. Preserve intent and constraints. Output at most 10 lines, with no preamble or explanation. Do not invent details. Do not use tools."
                }
            }
        });

    private void ConfigureHttpAuth(string password)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string BuildProviderError(HttpStatusCode statusCode, string body)
    {
        var safeDetail = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                safeDetail = " " + error.GetString();
        }
        catch { }
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Zenの認証に失敗しました。APIキーを確認してください。",
            HttpStatusCode.TooManyRequests => "Zenの利用上限に達したようです。時間をおいて再試行してください。",
            _ => $"OpenCode Zenからエラー ({(int)statusCode}) が返りました。{safeDetail}".Trim()
        };
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
        _sessionPassword = null;
        if (process is null) return;
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
        var workspace = _workspace;
        _workspace = null;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }
}
