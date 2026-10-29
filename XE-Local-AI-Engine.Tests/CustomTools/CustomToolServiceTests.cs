namespace XE_Local_AI_Engine.Tests.CustomTools;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
public sealed class CustomToolServiceTests
{
    [Test]
    public async Task Create_RejectsWhenDangerAcknowledgementMissing()
    {
        var service = BuildService(out _, out _);

        // The danger acknowledgement is enforced server-side, so a client that skips the checkbox cannot author.
        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Acknowledged = false
            }));
    }

    [Test]
    public async Task Create_RejectsInterpreterExecutable()
    {
        var service = BuildService(out _, out _);

        // A shell/interpreter executable reopens arbitrary-script execution and is rejected.
        var definition = ValidCommand() with
        {
            Command = ValidCommand().Command! with
            {
                Executable = "/bin/bash"
            }
        };

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(definition));
    }

    [Test]
    public async Task Create_RejectsNameCollisionWithKnownTool()
    {
        var service = BuildService(out _, out var offerProvider);
        // CustomToolService validates built-in/MCP collisions against the SYNC known-names view by design (the async view
        // also lists custom tools, which would self-collide on update), so the mock configures the sync method.
#pragma warning disable CA1849, S6966 // configures the sync overload on purpose; it is the view under test
        offerProvider.GetKnownToolNames().Returns(["custom__weather"]);
#pragma warning restore CA1849, S6966

        // The normalized name collides with an existing built-in/MCP tool name.
        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Name = "weather"
            }));
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
            service.CreateAsync(definition));
    }

    [Test]
    public async Task Create_RejectsParameterizedHostWithoutAllowedHosts()
    {
        var service = BuildService(out _, out _);

        // A model-fillable host must be pinned to an operator allow-list.
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
            service.CreateAsync(definition));
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
            service.CreateAsync(definition));
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

        var view = await service.CreateAsync(definition);

        var secret = view.Http!.Headers.Single(header => header.Name == "Authorization");
        var plain = view.Http.Headers.Single(header => header.Name == "Accept");

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

        var view = await service.CreateAsync(ValidHttpFetch());

        AssertEx.Equal("custom__weather", view.Name);
        await store.Received(1).CreateAsync(Arg.Is<CustomToolInput>(input => input.Name == "custom__weather"), Arg.Any<CancellationToken>());
    }

    [Test]
    public void ProbeExecutable_RejectsBlankPath()
    {
        var service = BuildService(out _, out _);

        var result = service.ProbeExecutable("   ");

        AssertEx.False(result.Ok);
    }

    [Test]
    public async Task Create_RejectsAllowUnattendedOnParameterizedTool()
    {
        var service = BuildService(out _, out _);

        // Unattended use is a verbatim invocation: a Parameterized tool would let a model fill part of it.
        var definition = ValidHttpFetch() with
        {
            Mode = CustomToolMode.Parameterized,
            AllowUnattended = true,
            Parameters =
            [
                new CustomToolParameterModel
                {
                    Name = "city",
                    Type = "string",
                    Required = true
                }
            ],
            Http = ValidHttpFetch().Http! with
            {
                UrlTemplate = "https://api.example.com/weather/{city}"
            }
        };

        var exception = await AssertEx.ThrowsAsync<CustomToolValidationException>(() => service.CreateAsync(definition));
        AssertEx.Contains(exception.Message, "Fixed mode");
    }

    [Test]
    public async Task Create_RejectsAllowUnattendedWithoutAcknowledgement()
    {
        var service = BuildService(out _, out _);

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidCommand() with
            {
                AllowUnattended = true,
                Acknowledged = false
            }));
    }

    [Test]
    [Arguments(CustomToolKind.HttpFetch)]
    [Arguments(CustomToolKind.Command)]
    public async Task Create_PersistsAllowUnattendedOnFixedTool(CustomToolKind kind)
    {
        var service = BuildService(out var store, out _);
        var definition = (kind == CustomToolKind.HttpFetch ? ValidHttpFetch() : ValidCommand()) with
        {
            AllowUnattended = true
        };

        var view = await service.CreateAsync(definition);

        AssertEx.True(view.AllowUnattended, "The view must echo the stored flag.");
        await store.Received(1).CreateAsync(Arg.Is<CustomToolInput>(input => input.AllowUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_ThreadsAllowUnattendedIntoTheInput()
    {
        var service = BuildService(out var store, out _);
        var id = Guid.NewGuid();
        store.UpdateAsync(id, Arg.Any<CustomToolInput>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult<CustomToolRecord?>(ToRecord(call.Arg<CustomToolInput>())));

        var view = await service.UpdateAsync(id, ValidCommand() with
        {
            AllowUnattended = true
        });

        // Before ADR 0021 every update wrote false, silently clearing the flag.
        AssertEx.True(AssertEx.NotNull(view).AllowUnattended, "An update must keep the flag the client sent.");
        await store.Received(1).UpdateAsync(id, Arg.Is<CustomToolInput>(input => input.AllowUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("192.168.1.20")]
    [Arguments("fd00::20")]
    [Arguments("homeassistant.lan")]
    public async Task Create_AcceptsListedPrivateHostAndEchoesIt(string host)
    {
        var service = BuildService(out _, out _);
        var urlHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;

        var view = await service.CreateAsync(ValidHttpFetch() with
        {
            AllowUnattended = true,
            Http = ValidHttpFetch().Http! with
            {
                UrlTemplate = $"http://{urlHost}:8123/api/services/light/turn_on",
                AllowedHosts = [host],
                PrivateHosts = [host]
            }
        });

        AssertEx.Equal(host, string.Join(",", AssertEx.NotNull(view.Http).PrivateHosts));
    }

    [Test]
    public async Task Create_RejectsFixedPrivateLiteralThatIsNotListed()
    {
        var service = BuildService(out _, out _);

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Http = ValidHttpFetch().Http! with
                {
                    UrlTemplate = "http://192.168.1.21/api",
                    AllowedHosts = ["192.168.1.20", "192.168.1.21"],
                    PrivateHosts = ["192.168.1.20"]
                }
            }));
    }

    [Test]
    public async Task Create_RejectsPrivateHostMissingFromAllowedHosts()
    {
        var service = BuildService(out _, out _);

        var exception = await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Http = ValidHttpFetch().Http! with
                {
                    UrlTemplate = "http://192.168.1.20/api",
                    AllowedHosts = [],
                    PrivateHosts = ["192.168.1.20"]
                }
            }));
        AssertEx.Contains(exception.Message, "allowedHosts");
    }

    [Test]
    [Arguments("http://192.168.1.20")]
    [Arguments("192.168.1.20:8123")]
    [Arguments("*.lan")]
    [Arguments("user@homeassistant.lan")]
    [Arguments("homeassistant.lan/api")]
    [Arguments("0300.0250.1.20")]
    [Arguments("[fd00::20]")]
    public async Task Create_RejectsMalformedPrivateHost(string entry)
    {
        var service = BuildService(out _, out _);

        var exception = await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Http = ValidHttpFetch().Http! with
                {
                    AllowedHosts = [entry],
                    PrivateHosts = [entry]
                }
            }));
        AssertEx.Contains(exception.Message, "exact host name");
    }

    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("169.254.169.254")]
    [Arguments("fe80::1")]
    [Arguments("224.0.0.1")]
    [Arguments("0.0.0.0")]
    public async Task Create_RejectsAlwaysDeniedPrivateHost(string entry)
    {
        var service = BuildService(out _, out _);

        var exception = await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidHttpFetch() with
            {
                Http = ValidHttpFetch().Http! with
                {
                    AllowedHosts = [entry],
                    PrivateHosts = [entry]
                }
            }));
        AssertEx.Contains(exception.Message, "stays denied");
    }

    [Test]
    public async Task Create_DetachedCommand_RoundTripsTheFlag()
    {
        var service = BuildService(out _, out _);

        var view = await service.CreateAsync(ValidCommand() with
        {
            Command = ValidCommand().Command! with
            {
                TimeoutSeconds = 0,
                Detach = true
            }
        });

        AssertEx.True(AssertEx.NotNull(view.Command).Detach, "The view must echo the stored detach flag.");
    }

    [Test]
    public async Task Create_RejectsDetachedCommandWithATimeout()
    {
        var service = BuildService(out _, out _);

        var exception = await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidCommand() with
            {
                Command = ValidCommand().Command! with
                {
                    Detach = true
                }
            }));
        AssertEx.Contains(exception.Message, "timeout must be 0");
    }

    [Test]
    public async Task Create_RejectsPrivateHostsOnCommandTool()
    {
        var service = BuildService(out _, out _);

        await AssertEx.ThrowsAsync<CustomToolValidationException>(() =>
            service.CreateAsync(ValidCommand() with
            {
                Http = new HttpFetchDefinition
                {
                    AllowedHosts = ["192.168.1.20"],
                    PrivateHosts = ["192.168.1.20"]
                }
            }));
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

        return new CustomToolService(store, offerProvider, StubNodeRuntimeSettings.Create().Build());
    }

    private static CustomToolRecord ToRecord(CustomToolInput input)
    {
        return new CustomToolRecord
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = input.Description,
            Kind = input.Kind,
            Mode = input.Mode,
            ParametersJson = input.ParametersJson,
            ConfigJson = input.ConfigJson,
            Enabled = input.Enabled,
            Acknowledged = input.Acknowledged,
            AllowUnattended = input.AllowUnattended,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
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

    [Test]
    public async Task GetUpdateDelete_WhenTheIdIsUnknown_ThrowNotFound()
    {
        var service = BuildService(out var store, out _);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<CustomToolRecord?>(null));
        store.UpdateAsync(id, Arg.Any<CustomToolInput>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<CustomToolRecord?>(null));
        store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        _ = await AssertEx.ThrowsAsync<CustomToolNotFoundException>(async () => await service.GetByIdAsync(id));
        _ = await AssertEx.ThrowsAsync<CustomToolNotFoundException>(async () => await service.UpdateAsync(id, ValidCommand()));
        _ = await AssertEx.ThrowsAsync<CustomToolNotFoundException>(async () => await service.DeleteAsync(id));
    }
}
