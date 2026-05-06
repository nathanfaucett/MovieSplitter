using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MovieSplitter.Detection.Ollama;

public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaClient(string baseUrl, string model, ILogger logger)
    {
        _model  = model;
        _logger = logger;
        _http   = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout     = TimeSpan.FromSeconds(120)
        };
    }

    /// <summary>
    /// Sends a prompt and returns the raw text response.
    /// Throws HttpRequestException on network failure.
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        var payload = new OllamaRequest
        {
            Model   = _model,
            Prompt  = prompt,
            Stream  = false,
            Options = new OllamaOptions { Temperature = 0.0 }
        };

        _logger.LogDebug("[Ollama] POST /api/generate model={M} prompt_len={L}",
            _model, prompt.Length);

        var resp = await _http.PostAsJsonAsync("api/generate", payload, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();

        var result = await resp.Content.ReadFromJsonAsync<OllamaResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Empty response from Ollama");

        return result.Response;
    }
}

file record OllamaRequest
{
    public required string Model   { get; init; }
    public required string Prompt  { get; init; }
    public bool            Stream  { get; init; }
    public OllamaOptions?  Options { get; init; }
}

file record OllamaOptions
{
    public double Temperature { get; init; }
}

file record OllamaResponse
{
    public string Response { get; init; } = "";
}
