namespace XE_Local_AI_Engine.Tests.ExternalApps;

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
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal) { ["ADMIN_PASSWORD"] = Secret });

        AssertEx.Equal(Secret, plan.Services[0].Specification.Environment["PASSWORD"]);
    }

    [Test]
    public void Plan_WithNoSuppliedValue_FallsBackToTheDeclaredDefault()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", environment: Env("USER", "${ADMIN_USER}"))],
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
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", environment: Env("TAVILY_API_KEY", "${TAVILY_API_KEY}"))],
            variables: [ExternalAppTestManifests.Variable("TAVILY_API_KEY")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal));

        AssertEx.True(plan.Services[0].Specification.Environment.ContainsKey("TAVILY_API_KEY"), "The key must still be emitted.");
        AssertEx.Equal(string.Empty, plan.Services[0].Specification.Environment["TAVILY_API_KEY"]);
    }

    [Test]
    public void Plan_WithARequiredVariableWithNoValueAndNoDefault_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var exception = AssertEx.Throws<ExternalAppConfigurationException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Contains(exception.Message, "ADMIN_PASSWORD");
    }

    [Test]
    public void Plan_CarriesEveryBuiltIn()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("app", environment: Env("NTFY_BASE_URL", "http://127.0.0.1:${XE_UI_HOST_PORT_ntfy-server}")),
            ExternalAppTestManifests.Service("ntfy-server", ports: [ExternalAppTestManifests.UiPort(80)], image: ExternalAppTestManifests.SecondImage)
        ]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal), [new ExternalAppHostPort("ntfy-server", 80, 8091)]);

        AssertEx.Equal("http://127.0.0.1:8091", plan.Services.Single(service => service.ServiceName == "app").Specification.Environment["NTFY_BASE_URL"]);
    }

    [Test]
    public void Plan_WithATokenNamingAServiceThatPublishesNothing_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
        [
            ExternalAppTestManifests.Service("a", dependsOn: [new ApplicationDependency("b", "started")]),
            ExternalAppTestManifests.Service("b", dependsOn: [new ApplicationDependency("a", "started")], image: ExternalAppTestManifests.SecondImage)
        ]);

        _ = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Test]
    public void Plan_WithADependencyTheManifestDoesNotDeclare_Throws()
    {
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("a", dependsOn: [new ApplicationDependency("ghost", "started")])]);

        _ = AssertEx.Throws<ExternalAppManifestException>(() => Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The reason both halves of the layout are namespaced by service: a storage name is unique only within a
    ///     service, and a flat layout would back two containers with one host directory.
    /// </summary>
    [Test]
    public void Plan_WhenTwoServicesDeclareTheSameStorageName_UsesSeparateHostDirectories()
    {
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
        [
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
        var manifest = ExternalAppTestManifests.Manifest(
            [ExternalAppTestManifests.Service("app", environment: Env("PASSWORD", "${ADMIN_PASSWORD}"))],
            variables: [ExternalAppTestManifests.Variable("ADMIN_PASSWORD", required: true, type: "secret")]);

        var plan = Plan(manifest, new Dictionary<string, string>(StringComparer.Ordinal) { ["ADMIN_PASSWORD"] = Secret });

        AssertEx.False(plan.ToString().Contains(Secret, StringComparison.Ordinal), "A plan must not print its environment.");
        AssertEx.False(plan.Services[0].ToString().Contains(Secret, StringComparison.Ordinal), "A service deployment must not print its environment.");
        AssertEx.False(plan.Services[0].Specification.ToString().Contains(Secret, StringComparison.Ordinal), "A specification must not print its environment.");
    }

    private static DeploymentPlan Plan(ApplicationManifest manifest,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyList<ExternalAppHostPort>? uiHostPorts = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-planner", InstanceId.ToString("N"));
        var storage = new ExternalAppStoragePaths(root, Path.Combine(root, "volumes"), Path.Combine(root, "files"));
        var ports = uiHostPorts ?? DefaultPorts(manifest);

        return DeploymentPlanner.Plan(manifest, InstanceId, InstallId, variables, new ResolvedContainerIdentity(1234, 5678), ports, storage);
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
        return new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value };
    }
}
