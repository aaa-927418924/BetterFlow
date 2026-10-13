using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterFlow.Services;

internal sealed record GptLiveCredentials(string AccessToken, string RefreshToken, string AccountId, DateTimeOffset ExpiresAt);

internal sealed class GptLiveAuthService
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string CallbackPrefix = "http://localhost:1455/";
    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsSignedIn => TryLoadCredentials() is not null;

    public string GetAccountId() => TryLoadCredentials()?.AccountId
        ?? throw new InvalidOperationException("ChatGPTにサインインしてください。");

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AppPaths.EnsureDirectories();
            using var listener = new HttpListener();
            listener.Prefixes.Add(CallbackPrefix);
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException("サインイン用のlocalhostポート1455を開けませんでした。別のOpenAIサインインを終了してから再試行してください。", ex);
            }

            var verifier = CreateRandomBase64Url(48);
            var challenge = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = CreateRandomBase64Url(24);
            var authorize = new UriBuilder("https://auth.openai.com/oauth/authorize")
            {
                Query = BuildQuery(new Dictionary<string, string>
                {
                    ["response_type"] = "code",
                    ["client_id"] = ClientId,
                    ["redirect_uri"] = RedirectUri,
                    ["scope"] = "openid profile email offline_access",
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256",
                    ["state"] = state,
                    ["id_token_add_organizations"] = "true",
                    ["codex_cli_simplified_flow"] = "true",
                    ["originator"] = "better-flow"
                })
            }.Uri;

            Process.Start(new ProcessStartInfo(authorize.AbsoluteUri) { UseShellExecute = true });
            var code = await WaitForAuthorizationCodeAsync(listener, state, cancellationToken);
            using var tokenResponse = await ExchangeCodeAsync(code, verifier, cancellationToken);
            var credentials = ReadCredentials(tokenResponse.RootElement, null);
            SaveCredentials(credentials);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = TryLoadCredentials() ?? throw new InvalidOperationException("ChatGPTにサインインしてください。");
            if (credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return credentials.AccessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credentials.RefreshToken,
                    ["client_id"] = ClientId
                })
            };
            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ChatGPTのサインインを更新できませんでした (HTTP {(int)response.StatusCode})。設定から再度サインインしてください。");

            using var document = JsonDocument.Parse(body);
            var refreshed = ReadCredentials(document.RootElement, credentials);
            SaveCredentials(refreshed);
            return refreshed.AccessToken;
        }
        finally { _gate.Release(); }
    }

    public void SignOut()
    {
        if (File.Exists(AppPaths.GptLiveCredentialFile))
            File.Delete(AppPaths.GptLiveCredentialFile);
    }

    private static async Task<string> WaitForAuthorizationCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var request = context.Request;
            var response = context.Response;
            if (!string.Equals(request.Url?.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                continue;
            }

            var error = request.QueryString["error"];
            var code = request.QueryString["code"];
            var state = request.QueryString["state"];
            var success = string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code) &&
                          string.Equals(state, expectedState, StringComparison.Ordinal);
            var page = success
                ? "<html><meta charset='utf-8'><body style='font-family:Segoe UI,sans-serif'>サインインが完了しました。このタブを閉じてBetter Flowへ戻ってください。</body></html>"
                : "<html><meta charset='utf-8'><body style='font-family:Segoe UI,sans-serif'>サインインできませんでした。このタブを閉じてBetter Flowへ戻ってください。</body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
            response.Close();

            if (!success)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    throw new InvalidOperationException("ChatGPTのサインインがキャンセルまたは拒否されました。");
                throw new InvalidOperationException("サインイン応答を確認できませんでした。最初からやり直してください。");
            }
            return code!;
        }
    }

    private static async Task<JsonDocument> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = RedirectUri
            })
        };
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ChatGPTの認証トークンを取得できませんでした (HTTP {(int)response.StatusCode})。もう一度サインインしてください。");
        return JsonDocument.Parse(body);
    }

    private static GptLiveCredentials ReadCredentials(JsonElement root, GptLiveCredentials? previous)
    {
        var access = ReadString(root, "access_token");
        var refresh = ReadString(root, "refresh_token") ?? previous?.RefreshToken;
        var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var seconds)
            ? seconds
            : 0;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || lifetime <= 0)
            throw new InvalidDataException("ChatGPTから必要な認証情報が返りませんでした。サインインをやり直してください。");

        var accountId = ExtractAccountId(access) ?? previous?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId))
            throw new InvalidDataException("ChatGPTアカウントIDを認証トークンから確認できませんでした。サインインをやり直してください。");
        return new GptLiveCredentials(access, refresh, accountId, DateTimeOffset.UtcNow.AddSeconds(lifetime));
    }

    private static string? ExtractAccountId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
            return FindAccountId(payload.RootElement);
        }
        catch { return null; }
    }

    private static string? FindAccountId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("chatgpt_account_id", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("account_id", StringComparison.OrdinalIgnoreCase))
                {
                    var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                var nested = FindAccountId(property.Value);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindAccountId(child);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static GptLiveCredentials? TryLoadCredentials()
    {
        try
        {
            if (!File.Exists(AppPaths.GptLiveCredentialFile)) return null;
            var encrypted = File.ReadAllBytes(AppPaths.GptLiveCredentialFile);
            var plaintext = Dpapi.Unprotect(encrypted);
            try { return JsonSerializer.Deserialize<GptLiveCredentials>(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch { return null; }
    }

    private static void SaveCredentials(GptLiveCredentials credentials)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var protectedBytes = Dpapi.Protect(json);
        CryptographicOperations.ZeroMemory(json);
        var temporary = AppPaths.GptLiveCredentialFile + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);
        CryptographicOperations.ZeroMemory(protectedBytes);
        File.Move(temporary, AppPaths.GptLiveCredentialFile, overwrite: true);
    }

    private static string CreateRandomBase64Url(int length) => ToBase64Url(RandomNumberGenerator.GetBytes(length));
    private static string ToBase64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
        public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

        private static byte[] Transform(byte[] source, bool protect)
        {
            var pin = GCHandle.Alloc(source, GCHandleType.Pinned);
            var input = new DataBlob { Length = source.Length, Data = pin.AddrOfPinnedObject() };
            DataBlob output;
            try
            {
                var success = protect
                    ? CryptProtectData(ref input, "Better Flow GPT Live", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { pin.Free(); }

            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
    }
}
