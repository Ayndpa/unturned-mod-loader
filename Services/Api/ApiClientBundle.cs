using System.Net;
using UnturnedModLoader.Models;
using UnturnedModLoader.Models.Api;

namespace UnturnedModLoader.Services.Api;

public sealed class ApiClientBundle : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _downloadHttp;
    private readonly CookieContainer _cookies;
    private readonly Uri _baseUri;

    public IAuthApiClient Auth { get; }
    public IModsApiClient Mods { get; }
    public HttpClient SharedHttpClient => _http;

    public ApiClientBundle(AppSettings settings)
    {
        var baseUrl = ModsApiClientFactory.ResolveBaseUrl(settings);
        _baseUri = new Uri(baseUrl);
        _cookies = new CookieContainer();
        RestoreToken(settings);

        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _downloadHttp = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl + "/"),
            Timeout = TimeSpan.FromMinutes(10),
        };

        Auth = new HttpAuthApiClient(_http);
        Mods = new HttpModsApiClient(_http, _downloadHttp);
    }

    public void SaveSessionToSettings(AppSettings settings, AuthUser? user = null)
    {
        settings.AuthToken = _cookies.GetCookies(_baseUri)["token"]?.Value;

        if (user is not null)
        {
            settings.UserId = user.Id;
            settings.Username = user.Username;
            settings.UserRole = user.Role;
        }
    }

    public void SaveToken(AppSettings settings, string token)
    {
        settings.AuthToken = token;
        _cookies.Add(_baseUri, new Cookie("token", token, "/", _baseUri.Host));
    }

    public void ClearSession(AppSettings settings)
    {
        foreach (Cookie cookie in _cookies.GetCookies(_baseUri))
            cookie.Expired = true;

        settings.AuthToken = null;
        settings.UserId = null;
        settings.Username = null;
        settings.UserRole = null;
    }

    private void RestoreToken(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AuthToken))
            return;

        _cookies.Add(_baseUri, new Cookie("token", settings.AuthToken, "/", _baseUri.Host));
    }

    public void Dispose()
    {
        _downloadHttp.Dispose();
        _http.Dispose();
    }
}