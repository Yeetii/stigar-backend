using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Stigar.Core.Services.StravaClient.Model;

namespace Stigar.Core.Services.StravaClient;

public class AuthenticationApi(HttpClient _httpClient, IConfiguration configuration)
{
    readonly string ClientSecret = configuration.GetValue<string>("StravaClientSecret") ?? throw new InvalidOperationException("No strava client secret found");
    readonly string ClientId = configuration.GetValue<string>("StravaClientId") ?? "26280";
    const string BaseUrl = "https://www.strava.com/api/v3/oauth/token";
    const string RevokeUrl = "https://www.strava.com/oauth/revoke";

    public async Task<TokenResponse> RefreshToken(string refreshToken)
    {
        var postBodyValues = new Dictionary<string, string>
        {
            { "client_id", "26280" },
            { "client_secret", ClientSecret },
            { "refresh_token", refreshToken },
            { "grant_type", "refresh_token" }
        };

        return await CommonTokenExchange(postBodyValues);
    }

    public async Task<TokenResponse> TokenExcange(string authToken)
    {
        var postBodyValues = new Dictionary<string, string>
        {
            { "client_id", "26280" },
            { "client_secret", ClientSecret },
            { "code", authToken },
            { "grant_type", "authorization_code" }
        };

        return await CommonTokenExchange(postBodyValues);
    }

    /// <summary>
    /// Revokes a user's Strava grant. Best effort: callers must not let a failure
    /// block local account deletion. Strava returns an empty 200 for an unknown
    /// token, so this is safe to call during deletion.
    /// </summary>
    public async Task RevokeToken(string refreshToken, CancellationToken cancellationToken = default)
    {
        var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {credentials}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "token", refreshToken },
            { "token_type_hint", "refresh_token" }
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TokenResponse> CommonTokenExchange(Dictionary<string, string> content)
    {
        var encodedContent = new FormUrlEncodedContent(content);
        var response = await _httpClient.PostAsync(BaseUrl, encodedContent);

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Strava token exchange failed with {(int)response.StatusCode} {response.ReasonPhrase}. Response body: {responseBody}");

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(responseBody)
            ?? throw new JsonException($"Could not parse token response. Response body: {responseBody}");

        return tokenResponse;
    }
}