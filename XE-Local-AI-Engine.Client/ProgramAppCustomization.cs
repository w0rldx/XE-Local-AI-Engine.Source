namespace XE_Local_AI_Engine.Client;

/// <summary>
///     Test seam for <see cref="Program.CreateAppAsync" />: environment/web-root overrides must be applied at
///     WebApplicationOptions time, configuration before AddServices reads it, and <see cref="ConfigureBuilder" />
///     runs after every product registration (override services, call UseTestServer) just before Build().
/// </summary>
public sealed class ProgramAppCustomization
{
    public string? EnvironmentName { get; init; }

    public string? ContentRootPath { get; init; }

    public string? WebRootPath { get; init; }

    public IReadOnlyDictionary<string, string?>? Configuration { get; init; }

    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    public TextWriter? StandardOutput { get; init; }

    public TextWriter? StandardError { get; init; }

    public Action? BeforeOneShotCommand { get; init; }

    public Func<HttpClient>? StatusHttpClientFactory { get; init; }
}
