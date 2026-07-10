using System.Net.Http.Json;
using System.Text.Json;
using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public sealed class HttpAuthApiClient(HttpClient http) : IAuthApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AuthResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                "api/auth/login",
                new { username, password },
                JsonOptions,
                cancellationToken);

            return await ParseAuthResponseAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, null, MapException(ex));
        }
    }

    public async Task<AuthResult> RegisterAsync(
        string username,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                "api/auth/register",
                new { username, email, password },
                JsonOptions,
                cancellationToken);

            return await ParseAuthResponseAsync(response, cancellationToken);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, null, MapException(ex));
        }
    }

    public async Task<AuthResult> MeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync("api/auth/me", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new AuthResult(false, null, ParseErrorMessage(body) ?? $"请求失败 (HTTP {(int)response.StatusCode})");

            var payload = JsonSerializer.Deserialize<MeResponse>(body, JsonOptions);
            if (payload is null)
                return new AuthResult(false, null, "服务端返回了无效数据。");

            if (payload.Banned == true)
            {
                var reason = payload.BanReason;
                return new AuthResult(
                    false,
                    null,
                    string.IsNullOrWhiteSpace(reason) ? "账号已被封禁。" : $"账号已被封禁：{reason}");
            }

            if (payload.User is null)
                return new AuthResult(false, null, null);

            return new AuthResult(true, payload.User, null);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, null, MapException(ex));
        }
    }

    public async Task<AuthActionResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsync("api/auth/logout", null, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new AuthActionResult(true, null);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AuthActionResult(false, ParseErrorMessage(body) ?? $"请求失败 (HTTP {(int)response.StatusCode})");
        }
        catch (Exception ex)
        {
            return new AuthActionResult(false, MapException(ex));
        }
    }

    private static async Task<AuthResult> ParseAuthResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new AuthResult(false, null, ParseErrorMessage(body) ?? $"请求失败 (HTTP {(int)response.StatusCode})");

        var payload = JsonSerializer.Deserialize<AuthActionResponse>(body, JsonOptions);
        if (payload?.User is null)
            return new AuthResult(false, null, "服务端返回了无效数据。");

        return new AuthResult(true, new AuthUser
        {
            Id = payload.User.Id,
            Username = payload.User.Username,
            Role = payload.User.Role,
        }, null);
    }

    private static string? ParseErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch
        {
            // Ignore malformed error payloads.
        }

        return null;
    }

    private static string MapException(Exception ex) => ex switch
    {
        TaskCanceledException => "请求超时，请检查服务端是否已启动。",
        HttpRequestException => $"无法连接 API：{ex.Message}",
        _ => $"请求失败：{ex.Message}",
    };
}