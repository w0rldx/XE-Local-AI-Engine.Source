namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Testing.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Content tests over <see cref="SampleCatalogManifest" />, the hand-authored test-only catalog document. The
///     shipped seed is empty, so this fixture is where every manifest feature the engine has to honour is pinned:
///     both <c>dependsOn</c> conditions, the single open target, per-service storage sharing one storage name, the
///     inlined file asset, the capability add-back, the host-gateway disclosure, the required secret that ships no
///     default, all five variable types, the absence of any <c>user</c> key, and the <c>${…}</c> tokens resolving to
///     declared variables or built-ins.
///     The fixture is never shipped and never produced by the converter, so it also pins the C# side of the
///     canonical fingerprint: an edit that forgets to re-stamp <c>manifestSha256</c> fails here.
/// </summary>
[Category(TestCategories.Unit)]
public sealed partial class SampleCatalogManifestTests
{
    private static readonly ExternalAppCatalogValidationResult Validation = ExternalAppCatalogValidator.Validate(SampleCatalogManifest.RawJson);

    private static ApplicationManifest Sample =>
        AssertEx.NotNull(Validation.Document, $"the fixture must validate; first error: {(Validation.Errors.Count > 0 ? Validation.Errors[0] : "(none)")}")
                .Applications.Single();

    [Test]
    public void Fixture_PassesTheRealValidator()
    {
        AssertEx.True(Validation.IsValid,
            $"the fixture must pass the same gate a bundled or remote catalog does; first error: {(Validation.Errors.Count > 0 ? Validation.Errors[0] : "(none)")}");
        AssertEx.Equal(SampleCatalogManifest.ApplicationId, Sample.Id);
    }

    [Test]
    public void Fixture_CommittedFingerprint_MatchesAFreshCompute()
    {
        // The hash is stamped by hand, the way build_catalog.py stamps the real catalog's. An edit that forgets to
        // re-stamp it would produce a manifest every install flow rejects, silently, long after the edit.
        AssertEx.Equal(Sample.ManifestSha256, ExternalAppManifestFingerprint.Compute(Sample),
            "re-stamp manifestSha256 in sample-catalog-manifest.json after editing the fixture.");
    }

    [Test]
    public void Fixture_DeclaresBothDependsOnConditions()
    {
        var edges = Sample.Services
                          .SelectMany(service => service.DependsOn.Select(dependency => $"{service.Name}->{dependency.Service}:{dependency.Condition}"))
                          .Order(StringComparer.Ordinal)
                          .ToArray();

        AssertEx.Equal("api->cache:started,web->api:healthy", string.Join(',', edges));
        AssertEx.NotNull(Service("api").Healthcheck, "a 'healthy' edge is only valid against a service that declares a healthcheck.");
    }

    [Test]
    public void Fixture_PublishesOneUiPortAndDeclaresExactlyOneOpenTarget()
    {
        var published = Sample.Services
                              .SelectMany(service => service.Ports.Select(port => (service.Name, port)))
                              .ToArray();

        AssertEx.Equal(expected: 1, published.Length);
        AssertEx.Equal("web", published[0].Name);
        AssertEx.Equal("ui", published[0].port.Role);
        AssertEx.Equal(expected: 8080, published[0].port.ContainerPort);
        AssertEx.Equal("/", AssertEx.NotNull(published[0].port.OpenPath));
    }

    [Test]
    public void Fixture_SharesOneStorageNameAcrossTwoServices()
    {
        // Storage names are namespaced per service, so 'data' on two services is two directories, not one. That is
        // exactly the shape the layout's per-service namespacing exists for, and it has to stay exercised.
        AssertEx.Equal("/var/www", Service("web").Storage.Single(entry => string.Equals(entry.Name, "data", StringComparison.Ordinal)).ContainerPath);
        AssertEx.Equal("/var/lib/api", Service("api").Storage.Single(entry => string.Equals(entry.Name, "data", StringComparison.Ordinal)).ContainerPath);
    }

    [Test]
    public void Fixture_EveryFileEntry_MatchesItsDeclaredSha256()
    {
        var files = Sample.Services.SelectMany(service => service.Files).ToArray();

        AssertEx.NotEmpty(files);
        foreach (var file in files)
        {
            AssertEx.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(file.ContentBase64))),
                $"file '{file.Source}' does not hash to its declared sha256.");
        }
    }

    [Test]
    public void Fixture_CapAddIsNonEmptyAndWithinTheAllowList()
    {
        var added = Sample.Services.SelectMany(service => service.CapAdd).ToArray();

        AssertEx.NotEmpty(added);
        foreach (var capability in added)
        {
            AssertEx.Contains(ExternalAppCatalogValidator.AllowedCapAdd, capability,
                $"capability '{capability}' is outside Docker's default set.");
        }
    }

    [Test]
    public void Fixture_DisclosesTheHostGatewayItDeclares()
    {
        AssertEx.Equal("host-gateway", string.Join(',', Service("web").ExtraHosts));
        AssertEx.True(Sample.Permissions.LocalNetwork, "a service reaching the Docker host must disclose localNetwork.");
        AssertEx.True(Sample.Permissions.Internet, "rule A20 makes internet:false unreachable — V1 enforces no outbound restriction.");
    }

    [Test]
    public void Fixture_DeclaresEveryVariableTypeAndNoSecretWithADefault()
    {
        var byType = Sample.Variables.ToLookup(variable => variable.Type, StringComparer.Ordinal);

        foreach (var type in ExternalAppCatalogValidator.VariableTypes)
        {
            AssertEx.NotEmpty(byType[type].ToArray(), $"the fixture must declare at least one '{type}' variable.");
        }

        // D11: a secret never ships a default, and the required one exists so the install form's required-secret
        // path stays exercised against a manifest that validates.
        AssertEx.ContainsSingle(byType["secret"].ToArray(), variable => variable.Required);
        AssertEx.True(byType["secret"].All(variable => variable.Default is null), "a secret variable must never ship a default.");
    }

    [Test]
    public void Fixture_EnvironmentTokens_AllResolveToDeclaredVariablesOrBuiltIns()
    {
        var declared = Sample.Variables.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        var servicesWithUiPort = Sample.Services.Where(service => service.Ports.Count > 0).Select(service => service.Name).ToHashSet(StringComparer.Ordinal);
        var seen = 0;

        foreach (var service in Sample.Services)
        {
            foreach (var (key, value) in service.Environment)
            {
                foreach (var match in TokenPattern().Matches(value).Cast<Match>())
                {
                    seen++;
                    var token = match.Groups[1].Value;
                    var resolves = declared.Contains(token)
                                   || ExternalAppCatalogValidator.BuiltInVariableNames.Contains(token)
                                   || (token.StartsWith(ExternalAppCatalogValidator.BuiltInUiPortPrefix, StringComparison.Ordinal)
                                       && servicesWithUiPort.Contains(token[ExternalAppCatalogValidator.BuiltInUiPortPrefix.Length..]));

                    AssertEx.True(resolves, $"{service.Name}.{key}: '${{{token}}}' resolves to no declared variable, built-in, or published UI port.");
                }
            }
        }

        // Every declared variable plus one built-in and one UI-port token; a fixture that stopped carrying tokens
        // would make the loop above vacuous.
        AssertEx.Equal(Sample.Variables.Count + 2, seen);
    }

    [Test]
    public void Fixture_DeclaresNoUserField()
    {
        // R1-14/R1-26: application containers start as the image's default user and the engine never passes --user.
        // The contract carries no such member, so a 'user' key in an authored document round-trips away unnoticed
        // instead of failing validation — the JSON has to be walked, not the deserialized graph. The shipped seed
        // declares no application, so this fixture is the only authored document the walk can find one in.
        var offenders = new List<string>();
        CollectPropertyPaths(JsonNode.Parse(SampleCatalogManifest.RawJson)!, path: "$", "user", offenders);

        AssertEx.NotEmpty(Sample.Services);
        AssertEx.Empty(offenders, $"the fixture declares a 'user' key at: {string.Join(", ", offenders)}");
    }

    private static void CollectPropertyPaths(JsonNode node, string path, string propertyName, List<string> found)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (key, value) in jsonObject)
                {
                    if (string.Equals(key, propertyName, StringComparison.Ordinal))
                    {
                        found.Add($"{path}.{key}");
                    }

                    if (value is not null)
                    {
                        CollectPropertyPaths(value, $"{path}.{key}", propertyName, found);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is { } item)
                    {
                        CollectPropertyPaths(item, $"{path}[{index}]", propertyName, found);
                    }
                }

                break;

            default:
                break;
        }
    }

    private static ApplicationService Service(string name) =>
        Sample.Services.Single(service => string.Equals(service.Name, name, StringComparison.Ordinal));

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_-]*)\}")]
    private static partial Regex TokenPattern();
}
