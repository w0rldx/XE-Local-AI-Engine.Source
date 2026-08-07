namespace XE_Local_AI_Engine.Tests.CustomTools;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CustomToolServiceTests
{
    [Test]
    public async Task Create_RejectsWhenDangerAcknowledgementMissing()
    {
        var service = BuildService(out _, out _);

        // M2: the danger acknowledgement is enforced server-side, so a client that skips the checkbox cannot author.
        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Acknowledged = false
            })).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_RejectsInterpreterExecutable()
    {
        var service = BuildService(out _, out _);

        // C1: a shell/interpreter executable reopens arbitrary-script execution and is rejected.
        var definition = ValidCommand() with
        {
            Command = ValidCommand().Command! with
            {
                Executable = "/bin/bash"
            }
        };

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(definition)).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_RejectsNameCollisionWithKnownTool()
    {
        var service = BuildService(out _, out var offerProvider);
        // CustomToolService validates built-in/MCP collisions against the SYNC known-names view by design (the async view
        // also lists custom tools, which would self-collide on update), so the mock configures the sync method.
#pragma warning disable CA1849, S6966
        offerProvider.GetKnownToolNames().Returns(["custom__weather"]);
#pragma warning restore CA1849, S6966

        // The normalized name collides with an existing built-in/MCP tool name.
        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Name = "weather"
            })).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_RejectsUndeclaredPlaceholder()
    {
        var service = BuildService(out _, out _);

        // A Fixed tool declares no parameters, so any {token} in a template is undeclared and fails closed.
        var definition = ValidCommand() with
        {
            Command = ValidCommand().Command! with
            {
                ArgsTemplate = ["{city}"]
            }
        };

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(definition)).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_RejectsParameterizedHostWithoutAllowedHosts()
    {
        var service = BuildService(out _, out _);

        // H2: a model-fillable host must be pinned to an operator allow-list.
        var definition = ValidHttpFetch() with
        {
            Mode = CustomToolMode.Parameterized,
            Parameters =
            [
                new CustomToolParameterModel
                {
                    Name = "host",
                    Type = "string",
                    Required = true
                }
            ],
            Http = new HttpFetchDefinition
            {
                Method = "GET",
                UrlTemplate = "https://{host}/status",
                Headers = [],
                AllowedHosts = []
            }
        };

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(definition)).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_RejectsFixedHostTargetingLoopback()
    {
        var service = BuildService(out _, out _);

        // The fixed-host pre-validation runs the SSRF guard at author time. A loopback IP literal is decidable from the
        // URL alone, so it is rejected before save (a DNS name like "localhost" is caught later at connect time instead).
        var definition = ValidHttpFetch() with
        {
            Http = new HttpFetchDefinition
            {
                Method = "GET",
                UrlTemplate = "http://127.0.0.1/admin",
                Headers = [],
                AllowedHosts = []
            }
        };

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(definition)).ConfigureAwait(false);
    }

    [Test]
    public async Task Create_MasksSecretHeaderValueOnResponse()
    {
        var service = BuildService(out _, out _);

        var definition = ValidHttpFetch() with
        {
            Http = new HttpFetchDefinition
            {
                Method = "GET",
                UrlTemplate = "https://api.example.com/data",
                Headers =
                [
                    new CustomToolHeaderModel
                    {
                        Name = "Authorization",
                        Value = "Bearer topsecret",
                        IsSecret = true
                    },
                    new CustomToolHeaderModel
                    {
                        Name = "Accept",
                        Value = "application/json",
                        IsSecret = false
                    }
                ],
                AllowedHosts = []
            }
        };

        var view = await service.CreateAsync(definition).ConfigureAwait(false);

        var secret = view.Http!.Headers.Single(header => header.Name == "Authorization");
        var plain = view.Http!.Headers.Single(header => header.Name == "Accept");

        // The secret value never leaves the node — it comes back as the sentinel; the non-secret value is echoed verbatim.
        AssertEx.Equal(CustomToolSecrets.Sentinel, secret.Value);
        AssertEx.True(secret.IsSecret);
        AssertEx.Equal("application/json", plain.Value);
    }

    [Test]
    public void CompiledParameterizedSchema_ContainsNoGbnfBannedKeyword()
    {
        // The service asserts the compiled schema carries no GBNF-unsafe keyword; the compiler is safe by construction,
        // so this guards the invariant the assertion depends on (a length/range/format bound breaks the llama.cpp sampler).
        var parameters = new List<CustomToolParameterModel>
        {
            new()
            {
                Name = "city",
                Type = "string",
                Description = "The city",
                Required = true
            },
            new()
            {
                Name = "days",
                Type = "integer",
                Description = "Forecast horizon",
                Required = false
            }
        };

        var schema = CustomToolSchemaCompiler.Compile(CustomToolMode.Parameterized,
            [.. parameters.Select(static p => new CustomToolParameter(p.Name, p.Type, p.Description, p.Required))]);

        foreach (var banned in CustomToolSchemaCompiler.BannedSchemaKeywords)
        {
            AssertEx.False(schema.Contains(banned, StringComparison.Ordinal), $"schema leaked banned keyword {banned}");
        }
    }

    [Test]
    public async Task Create_PersistsValidHttpFetchTool()
    {
        var service = BuildService(out var store, out _);

        var view = await service.CreateAsync(ValidHttpFetch()).ConfigureAwait(false);

        AssertEx.Equal("custom__weather", view.Name);
        await store.Received(1).CreateAsync(Arg.Is<CustomToolInput>(input => input.Name == "custom__weather"), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void ProbeExecutable_RejectsBlankPath()
    {
        var service = BuildService(out _, out _);

        var result = service.ProbeExecutable("   ");

        AssertEx.False(result.Ok);
    }

    private static ICustomToolService BuildService(out ICustomToolStore store, out ILocalToolOfferProvider offerProvider)
    {
        store = Substitute.For<ICustomToolStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CustomToolRecord>>([]));
        store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<CustomToolRecord?>(null));
        store.CreateAsync(Arg.Any<CustomToolInput>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(ToRecord(call.Arg<CustomToolInput>())));

        offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetKnownToolNames().Returns([]);

        return new CustomToolService(store, offerProvider);
    }

    private static CustomToolRecord ToRecord(CustomToolInput input)
    {
        return new CustomToolRecord(Guid.NewGuid(),
            input.Name,
            input.Description,
            input.Kind,
            input.Mode,
            input.ParametersJson,
            input.ConfigJson,
            input.Enabled,
            input.Acknowledged,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static CustomToolDefinition ValidHttpFetch()
    {
        return new CustomToolDefinition
        {
            Name = "weather",
            Description = "Fetches the weather",
            Kind = CustomToolKind.HttpFetch,
            Mode = CustomToolMode.Fixed,
            Acknowledged = true,
            Parameters = [],
            Http = new HttpFetchDefinition
            {
                Method = "GET",
                UrlTemplate = "https://api.example.com/weather",
                Headers = [],
                AllowedHosts = []
            }
        };
    }

    private static CustomToolDefinition ValidCommand()
    {
        return new CustomToolDefinition
        {
            Name = "lister",
            Description = "Lists things",
            Kind = CustomToolKind.Command,
            Mode = CustomToolMode.Fixed,
            Acknowledged = true,
            Parameters = [],
            Command = new CommandDefinition
            {
                Executable = "/usr/bin/list-things",
                ArgsTemplate = ["--all"],
                TimeoutSeconds = 10,
                Env = []
            }
        };
    }
}
