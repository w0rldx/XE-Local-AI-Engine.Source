namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Token resolution, ordering and the mount rules. The three substitution rules are asymmetric on purpose, and
///     each asymmetry is a bug someone would otherwise ship: a literal dollar rewritten, a Compose default passed
///     into a container verbatim, or an optional variable's key silently dropped.
/// </summary>
public sealed class DeploymentPlannerTests
{
    private const string InstallId = "install-1";
    private const string Secret = "s3cr3t-admin-password";

    private static readonly Guid InstanceId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public void Plan_SubstitutesADeclaredValue()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ADMIN_PASSWORD"] = Secret
        });

        AssertEx.Equal(Secret, plan.Services[0].Specification.Environment["PASSWORD"]);
    }

    [Test]
    public void Plan_WithNoSuppliedValue_FallsBackToTheDeclaredDefault()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("USER", "${ADMIN_USER}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_USER", required: true, @default: "admin")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        AssertEx.Equal("admin", plan.Services[0].Specification.Environment["USER"]);
    }

    /// <summary>
    ///     The key survives even though the value is empty: an image that branches on "is this variable present" must
    ///     see the same shape whether or not the user filled the optional field in.
    /// </summary>
    [Test]
    public void Plan_WithAnOptionalVariableWithNoValue_EmitsTheKeyAsEmpty()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("TAVILY_API_KEY", "${TAVILY_API_KEY}"))],
            variables: [ExternalAppTestManifests.Variable("TAVILY_API_KEY")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        AssertEx.True(plan.Services[0].Specification.Environment.ContainsKey("TAVILY_API_KEY"), "The key must still be emitted.");
        AssertEx.Equal(string.Empty, plan.Services[0].Specification.Environment["TAVILY_API_KEY"]);
    }

    [Test]
    public void Plan_WithARequiredVariableWithNoValueAndNoDefault_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var exception = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(exception.Message, "ADMIN_PASSWORD");
    }

    [Test]
    public void Plan_CarriesEveryBuiltIn()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app",
                environment: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PUID"] = "${XE_UID}",
                    ["PGID"] = "${XE_GID}",
                    ["INSTANCE"] = "${XE_INSTANCE_ID}"
                },
                ports: [ExternalAppTestManifests.UiPort(7000)])
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        var environment = plan.Services[0].Specification.Environment;
        AssertEx.Equal("1234", environment["PUID"]);
        AssertEx.Equal("5678", environment["PGID"]);
        AssertEx.Equal(InstanceId.ToString("N"), environment["INSTANCE"]);
    }

    /// <summary>
    ///     The hyphen in the token grammar exists only for this: the service-name suffix. A host-visible URL for one
    ///     service is built inside another service's environment, which is why the port cannot be daemon-assigned.
    /// </summary>
    [Test]
    public void Plan_ResolvesAHyphenatedServiceNameTokenInAnotherServicesEnvironment()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app", environment: Env("NTFY_BASE_URL", "http://127.0.0.1:${XE_UI_HOST_PORT_ntfy-server}")),
            ExternalAppTestManifests.Service("ntfy-server", ports: [ExternalAppTestManifests.UiPort(80)], image: ExternalAppTestManifests.SecondImage)
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal), [new ExternalAppHostPort("ntfy-server", 80, 8091)]);

        AssertEx.Equal("http://127.0.0.1:8091", plan.Services.Single(service => service.ServiceName == "app").Specification.Environment["NTFY_BASE_URL"]);
    }

    [Test]
    public void Plan_WithATokenNamingAServiceThatPublishesNothing_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app", environment: Env("URL", "http://127.0.0.1:${XE_UI_HOST_PORT_quiet}")),
            ExternalAppTestManifests.Service("quiet", image: ExternalAppTestManifests.SecondImage)
        ]);

        var exception = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(exception.Message, "XE_UI_HOST_PORT_quiet");
    }

    [Test]
    public void Plan_WithAnUnknownToken_ThrowsNamingIt()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("X", "${NOT_DECLARED}"))]);

        var exception = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(exception.Message, "NOT_DECLARED");
    }

    /// <summary>
    ///     A Compose-style default is an error, not a literal: passing <c>${FOO:-bar}</c> through verbatim would put
    ///     that text into the container as if it were the value.
    /// </summary>
    [Test]
    [Arguments("${FOO:-bar}")]
    [Arguments("${ }")]
    [Arguments("${FOO")]
    [Arguments("${9INVALID}")]
    public void Plan_WithACompoundOrUnterminatedToken_Throws(string value)
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("X", value))]);

        _ = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Test]
    [Arguments("price is 5$", "price is 5$")]
    [Arguments("$HOME/bin", "$HOME/bin")]
    [Arguments("a$b$c", "a$b$c")]
    public void Plan_WithABareDollar_KeepsItLiteral(string value, string expected)
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("X", value))]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        AssertEx.Equal(expected, plan.Services[0].Specification.Environment["X"]);
    }

    [Test]
    public void Plan_OrdersServicesSoEveryDependencyComesFirst()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app", dependsOn: [new ApplicationDependency("search", "healthy"), new ApplicationDependency("db", "started")]),
            ExternalAppTestManifests.Service("db", image: ExternalAppTestManifests.SecondImage),
            ExternalAppTestManifests.Service("search", image: ExternalAppTestManifests.SecondImage)
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        var order = plan.Services.Select(static service => service.ServiceName).ToArray();
        AssertEx.Equal("app", order[^1]);
        AssertEx.True(Array.IndexOf(order, "db") < Array.IndexOf(order, "app"), "A dependency must be created before its dependant.");
        AssertEx.True(Array.IndexOf(order, "search") < Array.IndexOf(order, "app"), "A dependency must be created before its dependant.");

        var app = plan.Services.Single(service => service.ServiceName == "app");
        AssertEx.True(app.DependsOn.Single(dependency => dependency.Service == "search").RequiresHealthy, "A 'healthy' condition must survive as RequiresHealthy.");
        AssertEx.False(app.DependsOn.Single(dependency => dependency.Service == "db").RequiresHealthy, "A 'started' condition must not demand health.");
    }

    [Test]
    public void Plan_WithADependencyCycle_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("a", dependsOn: [new ApplicationDependency("b", "started")]),
            ExternalAppTestManifests.Service("b", dependsOn: [new ApplicationDependency("a", "started")], image: ExternalAppTestManifests.SecondImage)
        ]);

        _ = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Test]
    public void Plan_WithADependencyTheManifestDoesNotDeclare_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("a", dependsOn: [new ApplicationDependency("ghost", "started")])]);

        _ = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The reason both halves of the layout are namespaced by service: a storage name is unique only within a
    ///     service, and a flat layout would back two containers with one host directory.
    /// </summary>
    [Test]
    public void Plan_WhenTwoServicesDeclareTheSameStorageName_UsesSeparateHostDirectories()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app", storage: [new ApplicationStorage("data", "/app/data")]),
            ExternalAppTestManifests.Service("db", storage: [new ApplicationStorage("data", "/chroma/chroma")], image: ExternalAppTestManifests.SecondImage)
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        var app = plan.Services.Single(service => service.ServiceName == "app").Specification.Mounts.Single();
        var db = plan.Services.Single(service => service.ServiceName == "db").Specification.Mounts.Single();
        AssertEx.NotEqual(app.HostPath, db.HostPath);
        AssertEx.Contains(app.HostPath, Path.Combine("volumes", "app", "data"));
        AssertEx.False(app.ReadOnly, "A storage mount is writable.");
    }

    [Test]
    public void Plan_WithTwoMountsAtOneContainerPath_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app",
                storage: [new ApplicationStorage("data", "/app/data")],
                files: [ExternalAppTestManifests.File("settings.yml", "/app/data", "body")])
        ]);

        _ = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     Two mounts backed by one host directory is the collision the per-service layout cannot prevent on its own:
    ///     a snapshot declaring one storage name twice would bind the same directory at two container paths, and the
    ///     second write would silently be the first one's data.
    /// </summary>
    [Test]
    public void Plan_WithTwoMountsBackedByOneHostDirectory_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("app",
                storage: [new ApplicationStorage("data", "/app/data"), new ApplicationStorage("data", "/app/other")])
        ]);

        var exception = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(exception.Message, "host directory");
    }

    [Test]
    public void Plan_ProducesTheEngineGeneratedNamesAndLabels()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        AssertEx.Equal("xe-app-" + InstanceId.ToString("N"), plan.NetworkName);
        AssertEx.Equal("xe-app-" + InstanceId.ToString("N") + "-app", plan.Services[0].ContainerName);
        AssertEx.Equal(plan.Services[0].ContainerName, plan.Services[0].Specification.Name);
        AssertEx.Equal("external-apps", plan.Services[0].Specification.Labels["com.xe-local-ai-engine.owner"]);
        AssertEx.Equal(InstallId, plan.Services[0].Specification.Labels["com.xe-local-ai-engine.install"]);
        AssertEx.Equal(InstanceId.ToString("N"), plan.Services[0].Specification.Labels["com.xe-local-ai-engine.external-app.instance"]);
        AssertEx.Equal("app", plan.Services[0].Specification.Labels["com.xe-local-ai-engine.external-app.service"]);
    }

    /// <summary>
    ///     The plan carries decrypted secrets, so its printers are suppressed the way the specification's is. One
    ///     <c>LogDebug("{Plan}", plan)</c> downstream would otherwise write an application's admin password to the
    ///     node log.
    /// </summary>
    [Test]
    public void Plan_ToString_NeverContainsASecret()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ADMIN_PASSWORD"] = Secret
        });

        AssertEx.False(plan.ToString().Contains(Secret, StringComparison.Ordinal), "A plan must not print its environment.");
        AssertEx.False(plan.Services[0].ToString().Contains(Secret, StringComparison.Ordinal), "A service deployment must not print its environment.");
        AssertEx.False(plan.Services[0].Specification.ToString().Contains(Secret, StringComparison.Ordinal), "A specification must not print its environment.");
    }

    /// <summary>
    ///     The two bridge built-ins arrive together, and only when the node actually opened a bridge. A container
    ///     told an endpoint without a credential could reach the listener and be refused by it; a credential without
    ///     an endpoint names nothing.
    /// </summary>
    [Test]
    public void BuiltIns_WhenABridgeGrantIsSupplied_CarryTheEndpointAndTheToken()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("OPENAI_BASE_URL", "http://${XE_BRIDGE_ENDPOINT}/llm/v1"))
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal), bridgeGrant: new ContainerBridgeGrant("192.0.2.10:18790", "aabb.ccdd"));

        AssertEx.Equal("http://192.0.2.10:18790/llm/v1", plan.Services[0].Specification.Environment["OPENAI_BASE_URL"]);
    }

    [Test]
    public void BuiltIns_WhenABridgeGrantIsSupplied_CarryTheTokenVerbatim()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("OPENAI_API_KEY", "${XE_BRIDGE_TOKEN}"))
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal), bridgeGrant: new ContainerBridgeGrant("192.0.2.10:18790", "aabb.ccdd"));

        AssertEx.Equal("aabb.ccdd", plan.Services[0].Specification.Environment["OPENAI_API_KEY"]);
    }

    /// <summary>
    ///     Without a grant the tokens are not silently blanked: they are undeclared, and the planner refuses the
    ///     manifest the same way it refuses any other unresolvable token. Injecting an empty endpoint instead would
    ///     fail later, inside the container, and far less clearly.
    /// </summary>
    [Test]
    [Arguments("XE_BRIDGE_ENDPOINT")]
    [Arguments("XE_BRIDGE_TOKEN")]
    public void BuiltIns_WithoutABridgeGrant_RefuseAManifestThatReferencesThem(string token)
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("SOME_SETTING", $"${{{token}}}"))
        ]);

        _ = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)),
            $"A node with no bridge must refuse a manifest that needs {token}, not hand the container an empty one.");
    }

    /// <summary>
    ///     The refusal above is correct and its default message is useless for the case that will actually happen:
    ///     the bridge switched off, or a host with no IPv4 interface it can bind. The user reads a manifest error
    ///     about a token they have never heard of. The message has to name the bridge and the setting instead.
    /// </summary>
    [Test]
    [Arguments("XE_BRIDGE_ENDPOINT")]
    [Arguments("XE_BRIDGE_TOKEN")]
    public void BuiltIns_WithoutABridgeGrant_SayWhichFeatureIsMissingRatherThanNamingAnUnknownToken(string token)
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("SOME_SETTING", $"${{{token}}}"))
        ]);

        var thrown = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(thrown.Message, "container bridge", message: "The operator has to learn which feature is missing, not which token failed to resolve.");
        AssertEx.Contains(thrown.Message, $"{ContainerBridgeOptions.SectionName}:{nameof(ContainerBridgeOptions.Enabled)}", message: "The message must name the setting that turns the bridge on.");
    }

    /// <summary>
    ///     <c>RequiresBridge</c> is what admission asks instead of trial-planning, so it has to agree with the
    ///     refusal above about the same manifest: a reference to either built-in needs a grant.
    /// </summary>
    [Test]
    [Arguments("XE_BRIDGE_ENDPOINT")]
    [Arguments("XE_BRIDGE_TOKEN")]
    public void RequiresBridge_ForAManifestThatReferencesABridgeBuiltIn_IsTrue(string token)
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("SOME_SETTING", $"http://${{{token}}}/llm/v1"))
        ]);

        AssertEx.True(DeploymentPlanner.RequiresBridge(manifest),
            $"A manifest that reads {token} cannot be planned without a grant, so admission has to see that coming.");
        _ = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)),
            "The two answers are about the same manifest and must not disagree.");
    }

    /// <summary>
    ///     Only a <c>${…}</c> reference counts. A manifest that merely spells a built-in's NAME in prose, or reads
    ///     an ordinary declared variable, plans fine without a bridge and must not be reported as needing one.
    /// </summary>
    [Test]
    public void RequiresBridge_ForAManifestThatReferencesNeither_IsFalse()
    {
        var manifest = ExternalAppTestManifests.Manifest([
                ExternalAppTestManifests.Service("web",
                    environment: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OPENAI_BASE_URL"] = "${LLM_HOST}",
                        ["NOTE"] = "point XE_BRIDGE_ENDPOINT at this yourself"
                    })
            ],
            variables: [ExternalAppTestManifests.Variable("LLM_HOST", @default: "http://localhost:11434")]);

        AssertEx.False(DeploymentPlanner.RequiresBridge(manifest), "Nothing here substitutes a bridge built-in.");
        AssertEx.NotNull(Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The bridge message must not swallow the generic one: a token the manifest simply never declared is a
    ///     different mistake and still has to be reported as itself.
    /// </summary>
    [Test]
    public void BuiltIns_ForATokenThatIsNotABridgeName_KeepTheGenericUndeclaredMessage()
    {
        var manifest = ExternalAppTestManifests.Manifest([
            ExternalAppTestManifests.Service("web", environment: Env("SOME_SETTING", "${XE_NOT_A_THING}"))
        ]);

        var thrown = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(thrown.Message, "neither a declared variable nor a built-in", message: "An undeclared token is not a missing bridge.");
    }

    /// <summary>
    ///     The grant is a credential, so a plan that printed it would put an application's bridge access into any log
    ///     line that formatted one.
    /// </summary>
    [Test]
    public void BridgeGrant_PrintsNothing()
    {
        var printed = new ContainerBridgeGrant("192.0.2.10:18790", "aabb.the-secret-half").ToString();

        AssertEx.False(printed.Contains("the-secret-half", StringComparison.Ordinal), "A grant carries a live credential and must print none of it.");
        AssertEx.True(printed.Contains(nameof(ContainerBridgeGrant), StringComparison.Ordinal));
    }

    /// <summary>
    ///     The planner's built-in names and the catalog validator's recognised list are written as literals in two
    ///     files. This is the assertion that keeps them from drifting apart, which would let a manifest validate and
    ///     then fail to plan.
    /// </summary>
    [Test]
    public void BuiltInNames_AreAllRecognisedByTheCatalogValidator()
    {
        var manifest = ExternalAppTestManifests.Manifest([ExternalAppTestManifests.Service("web")]);
        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal), bridgeGrant: new ContainerBridgeGrant("192.0.2.10:18790", "aabb.ccdd"));

        AssertEx.NotNull(plan);
        foreach (var name in new[]
                 {
                     "XE_UID",
                     "XE_GID",
                     "XE_INSTANCE_ID",
                     "XE_BRIDGE_ENDPOINT",
                     "XE_BRIDGE_TOKEN"
                 })
        {
            AssertEx.Contains(ExternalAppCatalogValidator.BuiltInVariableNames, name,
                $"The planner injects '{name}', so a manifest referencing it must validate.");
        }
    }

    private static DeploymentPlan Plan(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<ExternalAppHostPort>? uiHostPorts = null,
        ContainerBridgeGrant? bridgeGrant = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-planner", InstanceId.ToString("N"));
        var storage = new ExternalAppStoragePaths(root, Path.Combine(root, "volumes"), Path.Combine(root, "files"));
        var ports = uiHostPorts ?? DefaultPorts(manifest);

        return DeploymentPlanner.Plan(manifest, InstanceId, InstallId, variables, new ResolvedContainerIdentity(1234, 5678), ports, storage, bridgeGrant);
    }

    private static IReadOnlyList<ExternalAppHostPort> DefaultPorts(ApplicationManifest manifest)
    {
        var next = 40000;
        return
        [
            .. manifest.Services.SelectMany(service => service.Ports.Select(port => new ExternalAppHostPort(service.Name, port.ContainerPort, next++)))
        ];
    }

    private static Dictionary<string, string> Env(string key, string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = value
        };
    }
}
