using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunSpace.Server.Security;

public sealed class TurnstileRegistrationMiddleware
{
    private const string SiteverifyUrl =
        "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly RequestDelegate _next;
    private readonly ILogger<TurnstileRegistrationMiddleware> _logger;

    public TurnstileRegistrationMiddleware(
        RequestDelegate next,
        ILogger<TurnstileRegistrationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        string requestPath =
            $"{context.Request.PathBase}{context.Request.Path}";

        string? expectedAction = null;

        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (
                requestPath.EndsWith(
                    "/auth/register",
                    StringComparison.OrdinalIgnoreCase)
                ||
                requestPath.EndsWith(
                    "/auth/register-with-key",
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                expectedAction = "register";
            }
            else if (
                requestPath.EndsWith(
                    "/auth/login",
                    StringComparison.OrdinalIgnoreCase)
                ||
                requestPath.EndsWith(
                    "/auth/login-with-key",
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                expectedAction = "login";
            }
        }

        if (expectedAction is null)
        {
            await _next(context);
            return;
        }

        string secret =
            configuration["TURNSTILE_SECRET_KEY"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError(
                "TURNSTILE_SECRET_KEY is missing. Registration blocked.");

            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "turnstile_unavailable",
                "Human verification is temporarily unavailable.");

            return;
        }

        string? token;

        try
        {
            token = await ReadTurnstileTokenAsync(context);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "The registration request is not valid JSON.");

            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "turnstile_required",
                "Complete the human verification to continue.");

            return;
        }

        TurnstileResponse? verification;

        try
        {
            HttpClient client =
                httpClientFactory.CreateClient("turnstile");

            using var requestContent =
                new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["secret"] = secret,
                        ["response"] = token
                    });

            using HttpResponseMessage cloudflareResponse =
                await client.PostAsync(
                    SiteverifyUrl,
                    requestContent,
                    context.RequestAborted);

            if (!cloudflareResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Turnstile Siteverify returned HTTP {StatusCode}.",
                    (int)cloudflareResponse.StatusCode);

                await WriteErrorAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "turnstile_unavailable",
                    "Human verification could not be checked.");

                return;
            }

            verification =
                await cloudflareResponse.Content
                    .ReadFromJsonAsync<TurnstileResponse>(
                        cancellationToken:
                            context.RequestAborted);
        }
        catch (OperationCanceledException)
            when (!context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Turnstile Siteverify timed out.");

            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "turnstile_timeout",
                "Human verification timed out. Please try again.");

            return;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Turnstile Siteverify could not be reached.");

            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "turnstile_unavailable",
                "Human verification could not be reached.");

            return;
        }

        if (verification is null || !verification.Success)
        {
            string errorCodes =
                verification?.ErrorCodes is { Length: > 0 }
                    ? string.Join(",", verification.ErrorCodes)
                    : "unknown";

            _logger.LogInformation(
                "Turnstile rejected registration. Codes: {ErrorCodes}",
                errorCodes);

            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "turnstile_failed",
                "Human verification failed. Please try again.");

            return;
        }

        string configuredHostnames =
            configuration["TURNSTILE_EXPECTED_HOSTNAMES"]
            ?? "runspace.cloud,www.runspace.cloud";

        string[] allowedHostnames =
            configuredHostnames.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        bool hostnameAllowed =
            !string.IsNullOrWhiteSpace(verification.Hostname) &&
            allowedHostnames.Contains(
                verification.Hostname,
                StringComparer.OrdinalIgnoreCase);

        if (!hostnameAllowed)
        {
            _logger.LogWarning(
                "Turnstile returned unexpected hostname {Hostname}.",
                verification.Hostname ?? "(empty)");

            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "turnstile_hostname_failed",
                "Human verification came from an invalid hostname.");

            return;
        }

        if (!string.Equals(
                verification.Action,
                expectedAction,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Turnstile returned unexpected action {Action}.",
                verification.Action ?? "(empty)");

            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "turnstile_action_failed",
                "Human verification was not issued for registration.");

            return;
        }

        await _next(context);
    }

    private static async Task<string?> ReadTurnstileTokenAsync(
        HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            IFormCollection form =
                await context.Request.ReadFormAsync(
                    context.RequestAborted);

            return FirstNonEmpty(
                form["turnstileToken"].FirstOrDefault(),
                form["cf-turnstile-response"].FirstOrDefault());
        }

        context.Request.EnableBuffering();

        try
        {
            using JsonDocument document =
                await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken:
                        context.RequestAborted);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty(
                    "turnstileToken",
                    out JsonElement tokenElement))
            {
                return tokenElement.GetString();
            }

            if (root.TryGetProperty(
                    "cf-turnstile-response",
                    out JsonElement fallbackElement))
            {
                return fallbackElement.GetString();
            }

            return null;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    private static string? FirstNonEmpty(
        params string?[] values)
    {
        return values.FirstOrDefault(
            value => !string.IsNullOrWhiteSpace(value));
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string error,
        string message)
    {
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(
            new
            {
                success = false,
                error,
                message
            },
            cancellationToken:
                context.RequestAborted);
    }

    private sealed class TurnstileResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("hostname")]
        public string? Hostname { get; init; }

        [JsonPropertyName("action")]
        public string? Action { get; init; }

        [JsonPropertyName("error-codes")]
        public string[] ErrorCodes { get; init; } =
            Array.Empty<string>();
    }
}
