using System.Net.Http.Json;
using System.Text.Json;

namespace KanbanTasker.Core.GitHub;

public sealed class GitHubAuthentication(HttpClient http, string clientId, IGitHubTokenStore store)
{
    private readonly SemaphoreSlim gate = new(1);
    public bool Configured => !string.IsNullOrWhiteSpace(clientId);
    public async Task<GitHubDeviceCode> BeginAsync(CancellationToken ct = default)
    {
        if (!Configured) throw new InvalidOperationException("The GitHub App client ID has not been configured for this build.");
        var data = await PostAsync("https://github.com/login/device/code", new { client_id = clientId }, ct);
        if (data.TryGetProperty("error",out _)) throw new GitHubApiException("GitHub sign-in could not be completed. Check the public Client ID and enable Device Flow in the GitHub App.");
        try
        {
            return new(data.GetProperty("device_code").GetString()!, data.GetProperty("user_code").GetString()!,
                data.GetProperty("verification_uri").GetString()!, data.GetProperty("interval").GetInt32(), data.GetProperty("expires_in").GetInt32());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GitHubApiException("GitHub returned an incomplete response."); }
    }
    public async Task SignInAsync(GitHubDeviceCode code, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = Math.Max(5, code.Interval);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var data = await PostAsync("https://github.com/login/oauth/access_token", new
            { client_id = clientId, device_code = code.DeviceCode, grant_type = "urn:ietf:params:oauth:grant-type:device_code" }, ct);
            if (data.TryGetProperty("error", out var error))
            {
                if (error.GetString() == "authorization_pending") continue;
                if (error.GetString() == "slow_down") { interval += 5; continue; }
                throw new GitHubApiException("GitHub sign-in was cancelled, expired, or denied. Sign in again.");
            }
            await store.SaveAsync(ReadTokens(data), ct);
            return;
        }
        throw new GitHubApiException("GitHub sign-in expired. Sign in again.");
    }
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var tokens = await store.LoadAsync(ct) ?? throw new GitHubApiException("Sign in to GitHub to edit this board.");
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return tokens.AccessToken;
            if (tokens.RefreshExpiresAt <= DateTimeOffset.UtcNow) throw new GitHubApiException("GitHub sign-in expired. Sign in again.");
            var data = await PostAsync("https://github.com/login/oauth/access_token", new
            { client_id = clientId, refresh_token = tokens.RefreshToken, grant_type = "refresh_token" }, ct);
            if (data.TryGetProperty("error", out _)) throw new GitHubApiException("GitHub sign-in expired. Sign in again.");
            tokens = ReadTokens(data);
            await store.SaveAsync(tokens, ct);
            return tokens.AccessToken;
        }
        finally { gate.Release(); }
    }
    public Task SignOutAsync(CancellationToken ct = default) => store.DeleteAsync(ct);
    private async Task<JsonElement> PostAsync(string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("KanbanTasker-SoloSync");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new GitHubApiException("GitHub sign-in could not be completed. Check your connection and try again.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = JsonDocument.Parse(await WorkspaceJson.ReadAsync(stream, ct));
            return json.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new GitHubApiException("GitHub is unreachable. Cached boards are read-only."); }
        catch (JsonException) { throw new GitHubApiException("GitHub returned an invalid response. Refresh the board."); }
    }
    private static GitHubTokens ReadTokens(JsonElement data)
    {
        // Only expiring GitHub App tokens are accepted; no client secret is needed for device-flow refresh.
        if (!data.TryGetProperty("expires_in", out var expires) || !data.TryGetProperty("refresh_token", out var refresh)
            || !data.TryGetProperty("refresh_token_expires_in", out var refreshExpires))
            throw new GitHubApiException("Enable expiring user access tokens in the GitHub App registration.");
        try
        {
            var access = data.GetProperty("access_token").GetString();
            var renewal = refresh.GetString();
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(renewal) || expires.GetInt32() <= 0 || refreshExpires.GetInt32() <= 0)
                throw new GitHubApiException("GitHub returned an incomplete response.");
            return new(access,renewal,DateTimeOffset.UtcNow.AddSeconds(expires.GetInt32()),DateTimeOffset.UtcNow.AddSeconds(refreshExpires.GetInt32()));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new GitHubApiException("GitHub returned an incomplete response."); }
    }
}
