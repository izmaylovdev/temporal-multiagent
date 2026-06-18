using System.Text;
using System.Text.Json;

namespace AgentSandbox.Core;

/// <summary>Minimal Anthropic Messages API client over HttpClient. Host-side only.</summary>
public static class AnthropicClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public record Usage(int Input, int Output, int Total);

    public static async Task<(string Text, Usage Usage)> CompleteAsync(
        string system, string user, int maxTokens, CancellationToken ct = default)
    {
        var body = new
        {
            model = Settings.AnthropicModel,
            max_tokens = maxTokens,
            system,
            messages = new[] { new { role = "user", content = user } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Settings.AnthropicBaseUrl}/v1/messages");
        req.Headers.Add("x-api-key", Settings.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            // 4xx (except 429 rate limit) are caller errors and not worth retrying.
            if (status is 400 or 401 or 403 or 404 or 422)
                throw new AnthropicNonRetryableException(status, json);
            throw new AnthropicException(status, json);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        foreach (var block in root.GetProperty("content").EnumerateArray())
            if (block.GetProperty("type").GetString() == "text")
                sb.Append(block.GetProperty("text").GetString());

        var u = root.GetProperty("usage");
        var inp = u.GetProperty("input_tokens").GetInt32();
        var outp = u.GetProperty("output_tokens").GetInt32();
        return (sb.ToString().Trim(), new Usage(inp, outp, inp + outp));
    }
}

/// <summary>Retryable Anthropic failure (5xx / 429 / transport).</summary>
public class AnthropicException : Exception
{
    public int StatusCode { get; }
    public AnthropicException(int status, string body)
        : base($"Anthropic API error {status}: {Trim(body)}") => StatusCode = status;

    protected static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>Non-retryable Anthropic failure (auth / bad request). Matched by name in the
/// workflow's RetryPolicy.NonRetryableErrorTypes.</summary>
public sealed class AnthropicNonRetryableException : AnthropicException
{
    public AnthropicNonRetryableException(int status, string body) : base(status, body) { }
}
