namespace XE_Local_AI_Engine.Tests.ExternalApps.Catalog;

using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ExternalAppCatalogValidator" /> rule coverage: one test per rule id in the S1 rule table, each
///     mutating a single field of one canonical valid document and asserting the specific error text. The catalog is
///     the only thing standing between an authored manifest and a container the engine creates, so every rule needs a
///     negative case — a rule that is never exercised is a rule the next slice silently relies on.
/// </summary>
public sealed class ExternalAppCatalogValidatorTests
{
    private const string ApplicationsKey = "applications";
    private const string ServicesKey = "services";
    private const string VariablesKey = "variables";
    private const string EnvironmentKey = "environment";
    private const string PermissionsKey = "permissions";
    private const string ValidationKey = "validation";
    private const string AllowedValuesKey = "allowedValues";
    private const string DefaultKey = "default";
    private const string StorageKey = "storage";
    private const string HealthcheckKey = "healthcheck";
    private const string StaleFingerprint = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string ValidDocumentJson =
        """
        {
          "schemaVersion": 1,
          "generatedAtUtc": "2026-09-05T00:00:00Z",
          "applications": [
            {
              "id": "sample-app",
              "manifestVersion": 1,
              "manifestSha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "displayName": "Sample App",
              "summary": "A curated sample application used by the catalog validator tests.",
              "description": "A curated sample application used by the catalog validator tests.",
              "homepage": "https://example.com/sample",
              "license": "AGPL-3.0-or-later",
              "trust": "xeCatalog",
              "testedVersion": "1.0.0",
              "requires": ["containers", "networks", "bindStorage", "loopbackPortPublishing", "healthChecks", "restartPolicies", "logs", "imagePull"],
              "permissions": { "internet": true, "localNetwork": true, "hostFiles": "none", "gpu": "none" },
              "resources": { "minimumMemoryMb": 4096, "recommendedMemoryMb": 8192, "cpuHint": 2, "pidsLimit": 2048 },
              "services": [
                {
                  "name": "app",
                  "image": "ghcr.io/example/sample@sha256:1f0c0d6bd3ad19a51a5d0c1b2e3f4a5b6c7d8e9f00112233445566778899aabb",
                  "imageTag": "1.0.0",
                  "entrypoint": null,
                  "command": null,
                  "environment": {
                    "ADMIN_PASSWORD": "${SAMPLE_ADMIN_PASSWORD}",
                    "SIDECAR_URL": "http://sidecar:8080",
                    "SIDECAR_PORT": "${XE_UI_HOST_PORT_sidecar}",
                    "INSTANCE": "${XE_INSTANCE_ID}",
                    "PUID": "${XE_UID}",
                    "PGID": "${XE_GID}",
                    "OPTIONAL_TWEAK": "${SAMPLE_TWEAK}"
                  },
                  "ports": [ { "containerPort": 7000, "role": "ui", "preferredHostPort": 7000, "openPath": "/" } ],
                  "storage": [
                    { "name": "data", "containerPath": "/app/data" },
                    { "name": "logs", "containerPath": "/app/logs" }
                  ],
                  "files": [],
                  "healthcheck": {
                    "test": ["CMD-SHELL", "curl -f http://localhost:7000/ || exit 1"],
                    "intervalSeconds": 5,
                    "timeoutSeconds": 6,
                    "retries": 20,
                    "startPeriodSeconds": 10
                  },
                  "dependsOn": [ { "service": "sidecar", "condition": "healthy" } ],
                  "capAdd": ["CHOWN", "SETGID", "SETUID"],
                  "extraHosts": ["host-gateway"],
                  "readOnlyRootFilesystem": false
                },
                {
                  "name": "sidecar",
                  "image": "docker.io/example/sidecar@sha256:2f0c0d6bd3ad19a51a5d0c1b2e3f4a5b6c7d8e9f00112233445566778899aabb",
                  "imageTag": "2.3.4",
                  "entrypoint": null,
                  "command": ["serve"],
                  "environment": { "SIDECAR_HOME": "/var/lib/sidecar" },
                  "ports": [ { "containerPort": 8080, "role": "ui", "preferredHostPort": 8091, "openPath": null } ],
                  "storage": [ { "name": "cache", "containerPath": "/var/cache/sidecar" } ],
                  "files": [
                    {
                      "source": "files/sidecar/settings.yml",
                      "containerPath": "/tmp/sidecar-settings.yml",
                      "sha256": "778fb2bf3e6ae22f6cf14f374333b24961f9523cf1980560e5e49318d976f334",
                      "contentBase64": "c2FtcGxlOiB0cnVlCg=="
                    }
                  ],
                  "healthcheck": {
                    "test": ["CMD", "true"],
                    "intervalSeconds": 5,
                    "timeoutSeconds": 5,
                    "retries": 10,
                    "startPeriodSeconds": 0
                  },
                  "dependsOn": [],
                  "capAdd": [],
                  "extraHosts": [],
                  "readOnlyRootFilesystem": true
                }
              ],
              "variables": [
                {
                  "name": "SAMPLE_ADMIN_PASSWORD",
                  "label": "Admin password",
                  "description": "The password the sample application's admin account is created with.",
                  "type": "secret",
                  "required": true,
                  "default": null,
                  "allowedValues": null,
                  "validation": { "minLength": 8, "maxLength": 256, "pattern": null },
                  "advanced": false
                },
                {
                  "name": "SAMPLE_TWEAK",
                  "label": "Sample tweak",
                  "description": null,
                  "type": "enum",
                  "required": false,
                  "default": "fast",
                  "allowedValues": ["fast", "slow"],
                  "validation": null,
                  "advanced": true
                }
              ]
            }
          ]
        }
        """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void Validate_ValidDocument_Succeeds()
    {
        var result = ExternalAppCatalogValidator.Validate(BuildValidJson());

        AssertEx.True(result.IsValid, string.Join(" | ", result.Errors));
        AssertEx.NotNull(result.Document);
        AssertEx.Equal(expected: 1, result.Document!.Applications.Count);
        AssertEx.Equal("sample-app", result.Document.Applications[0].Id);
    }

    [Test]
    public void Validate_MalformedJson_DoesNotThrow()
    {
        AssertEx.DoesNotThrow(
            () => _ = ExternalAppCatalogValidator.Validate("{ \"applications\": [ "),
            "the validator must convert a parse failure into a validation failure");

        var result = ExternalAppCatalogValidator.Validate("{ \"applications\": [ ");

        AssertEx.False(result.IsValid);
        AssertEx.Null(result.Document);
    }

    [Test]
    public void Validate_DocumentWithOneBadApplication_RejectsTheWholeDocument()
    {
        var result = MutateValid(document =>
        {
            var second = Application(document).DeepClone().AsObject();
            second["id"] = "second-app";
            second["trust"] = "community";
            document[ApplicationsKey]!.AsArray().Add(second);
        });

        AssertEx.False(result.IsValid, "one invalid application must reject the whole catalog");
        AssertEx.Null(result.Document);
    }

    [Test]
    public void Validate_DocumentMissingEveryOptionalCollection_ReturnsFailureWithoutThrowing()
    {
        const string sparse = """{"schemaVersion":1,"generatedAtUtc":"2026-09-05T00:00:00Z","applications":[{"id":"ab"}]}""";

        ExternalAppCatalogValidationResult? result = null;
        AssertEx.DoesNotThrow(
            () => result = ExternalAppCatalogValidator.Validate(sparse),
            "a document that omits every collection must fail validation, not throw");

        AssertEx.False(AssertEx.NotNull(result).IsValid);
        AssertEx.Contains(result!.Errors, error => error.Contains(".requires is required.", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenServiceNameContainsAHyphen_ResolvesTheUiPortTokenAsABuiltIn()
    {
        var result = MutateValid(document =>
        {
            Service(document, index: 1)["name"] = "my-svc";
            Service(document, index: 0)[EnvironmentKey]!["SIDECAR_PORT"] = "${XE_UI_HOST_PORT_my-svc}";
            Service(document, index: 0)["dependsOn"]!.AsArray()[0]!["service"] = "my-svc";
        });

        AssertEx.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    // ---------------------------------------------------------------- document rules

    [Test]
    public void Validate_WhenRawJsonIsEmpty_ReportsD1()
    {
        AssertError(ExternalAppCatalogValidator.Validate(rawJson: null), "Catalog JSON is empty.");
        AssertError(ExternalAppCatalogValidator.Validate("   "), "Catalog JSON is empty.");
    }

    [Test]
    public void Validate_WhenRawJsonIsUnparseable_ReportsD2()
    {
        AssertError(ExternalAppCatalogValidator.Validate("{ not json"), "Catalog JSON could not be parsed:");
    }

    [Test]
    public void Validate_WhenRawJsonIsTheNullLiteral_ReportsD3()
    {
        AssertError(ExternalAppCatalogValidator.Validate("null"), "Catalog JSON deserialized to null.");
    }

    [Test]
    public void Validate_WhenSchemaVersionIsUnsupported_ReportsD4()
    {
        var result = MutateValid(document => document["schemaVersion"] = 2);

        AssertError(result, "Unsupported schemaVersion 2 (expected 1).");
    }

    [Test]
    public void Validate_WhenGeneratedAtUtcIsNotIso8601_ReportsD5()
    {
        var result = MutateValid(document => document["generatedAtUtc"] = "05/09/2026 00:00");

        AssertError(result, "generatedAtUtc must be an ISO-8601 UTC timestamp.");
    }

    [Test]
    public void Validate_WhenApplicationsArrayIsMissing_ReportsD6()
    {
        var result = MutateValid(document => document.Remove(ApplicationsKey));

        AssertError(result, "applications array is required.");
    }

    [Test]
    public void Validate_WhenTwoApplicationsShareAnId_ReportsD7()
    {
        var result = MutateValid(document =>
            document[ApplicationsKey]!.AsArray().Add(Application(document).DeepClone()));

        AssertError(result, "applications[1].id 'sample-app' is a duplicate.");
    }

    [Test]
    public void Validate_WhenARequiredNestedRecordIsMissing_ReportsD8()
    {
        var result = MutateValid(document => Application(document).Remove(PermissionsKey));

        AssertError(result, "applications[0].permissions is required.");
    }

    // ---------------------------------------------------------------- application rules

    [Test]
    public void Validate_WhenApplicationIdIsMalformed_ReportsA1()
    {
        var result = MutateValid(document => Application(document)["id"] = "Sample_App");

        AssertError(result, "applications[0].id must match ^[a-z][a-z0-9-]{1,40}$.");
    }

    [Test]
    public void Validate_WhenManifestVersionIsBelowOne_ReportsA2()
    {
        var result = MutateValid(document => Application(document)["manifestVersion"] = 0);

        AssertError(result, "applications[0].manifestVersion must be 1 or greater.");
    }

    [Test]
    public void Validate_WhenDisplayNameIsBlank_ReportsA3()
    {
        var result = MutateValid(document => Application(document)["displayName"] = "  ");

        AssertError(result, "applications[0].displayName is required and must be at most 80 characters.");
    }

    [Test]
    public void Validate_WhenSummaryIsTooLong_ReportsA4()
    {
        var result = MutateValid(document => Application(document)["summary"] = new string('s', count: 201));

        AssertError(result, "applications[0].summary is required and must be at most 200 characters.");
    }

    [Test]
    public void Validate_WhenDescriptionIsTooLong_ReportsA5()
    {
        var result = MutateValid(document => Application(document)["description"] = new string('d', count: 2049));

        AssertError(result, "applications[0].description is required and must be at most 2048 characters.");
    }

    [Test]
    public void Validate_WhenHomepageIsNotHttps_ReportsA6()
    {
        var result = MutateValid(document => Application(document)["homepage"] = "http://example.com/sample");

        AssertError(result, "applications[0].homepage must be an absolute https URL.");
    }

    [Test]
    public void Validate_WhenLicenseIsBlank_ReportsA7()
    {
        var result = MutateValid(document => Application(document)["license"] = "");

        AssertError(result, "applications[0].license is required.");
    }

    [Test]
    public void Validate_WhenTrustIsNotXeCatalog_ReportsA8()
    {
        var result = MutateValid(document => Application(document)["trust"] = "community");

        AssertError(result, "applications[0].trust must be 'xeCatalog'.");
    }

    [Test]
    public void Validate_WhenTestedVersionIsBlank_ReportsA9()
    {
        var result = MutateValid(document => Application(document)["testedVersion"] = "");

        AssertError(result, "applications[0].testedVersion is required.");
    }

    [Test]
    public void Validate_WhenRequiresNamesAnUnknownCapability_ReportsA10()
    {
        var result = MutateValid(document => Application(document)["requires"]!.AsArray().Add("teleportation"));

        AssertError(result, "applications[0].requires must be a non-empty distinct subset of");
    }

    [Test]
    public void Validate_WhenHostFilesIsOutsideTheEnum_ReportsA11()
    {
        var result = MutateValid(document => Application(document)[PermissionsKey]!["hostFiles"] = "readonly");

        AssertError(result, "applications[0].permissions.hostFiles must be one of none, readOnly, readWrite.");
    }

    [Test]
    public void Validate_WhenMinimumMemoryIsBelowTheFloor_ReportsA12()
    {
        var result = MutateValid(document => Application(document)["resources"]!["minimumMemoryMb"] = 128);

        AssertError(result, "applications[0].resources.minimumMemoryMb must be at least 256.");
    }

    [Test]
    public void Validate_WhenServicesArrayIsEmpty_ReportsA14()
    {
        var result = MutateValid(document => Application(document)[ServicesKey] = new JsonArray());

        AssertError(result, "applications[0].services must contain between 1 and 8 services.");
    }

    [Test]
    public void Validate_WhenServiceNamesCollideOrAreReserved_ReportsA15()
    {
        var duplicate = MutateValid(document => Service(document, index: 1)["name"] = "app");
        AssertError(duplicate, "applications[0].services[1].name 'app' is a duplicate.");

        var reserved = MutateValid(document => Service(document, index: 1)["name"] = "xe");
        AssertError(reserved, "applications[0].services[1].name 'xe' is reserved.");
    }

    [Test]
    public void Validate_WhenVariableNamesCollideOrUseTheReservedPrefix_ReportsA16()
    {
        var duplicate = MutateValid(document => Variable(document, index: 1)["name"] = "SAMPLE_ADMIN_PASSWORD");
        AssertError(duplicate, "applications[0].variables[1].name 'SAMPLE_ADMIN_PASSWORD' is a duplicate.");

        var reserved = MutateValid(document => Variable(document, index: 1)["name"] = "XE_CUSTOM");
        AssertError(reserved, "applications[0].variables[1].name 'XE_CUSTOM' must not start with the reserved prefix XE_.");
    }

    [Test]
    public void Validate_WhenTwoPortsCarryAnOpenPath_ReportsA17()
    {
        var result = MutateValid(document => Service(document, index: 1)["ports"]!.AsArray()[0]!["openPath"] = "/status");

        AssertError(result, "applications[0] must declare exactly one port with an openPath (found 2).");
    }

    [Test]
    public void Validate_WhenExtraHostsAreDeclaredWithoutLocalNetwork_ReportsA18()
    {
        var result = MutateValid(document => Application(document)[PermissionsKey]!["localNetwork"] = false);

        AssertError(result, "applications[0].permissions.localNetwork must be true when a service declares extraHosts.");
    }

    [Test]
    public void Validate_WhenAPortIsPublishedWithoutTheCapability_ReportsA19()
    {
        var result = MutateValid(document =>
        {
            var requires = new JsonArray();
            foreach (var capability in Application(document)["requires"]!.AsArray())
            {
                if (capability?.GetValue<string>() is { } name && !string.Equals(name, "loopbackPortPublishing", StringComparison.Ordinal))
                {
                    requires.Add(name);
                }
            }

            Application(document)["requires"] = requires;
        });

        AssertError(result, "applications[0].requires must contain 'loopbackPortPublishing' when a service publishes a port.");
    }

    [Test]
    public void Validate_WhenInternetAccessIsDenied_ReportsA20()
    {
        var result = MutateValid(document => Application(document)[PermissionsKey]!["internet"] = false);

        AssertError(result, "applications[0].permissions.internet must be true: outbound restriction is not supported in this version.");
    }

    [Test]
    public void Validate_WhenManifestSha256IsNotHex_ReportsA21()
    {
        var result = MutateValidWithoutRefingerprinting(document =>
            Application(document)[ExternalAppManifestFingerprint.HashPropertyName] = "not-a-fingerprint");

        AssertError(result, "applications[0].manifestSha256 must be 64 lowercase hex digits.");
    }

    [Test]
    public void Validate_WhenManifestSha256IsStale_ReportsA21()
    {
        var result = MutateValidWithoutRefingerprinting(document =>
            Application(document)[ExternalAppManifestFingerprint.HashPropertyName] = StaleFingerprint);

        AssertError(result, "applications[0].manifestSha256 does not match the canonical manifest fingerprint.");
    }

    // ---------------------------------------------------------------- service rules

    [Test]
    public void Validate_WhenServiceNameIsMalformed_ReportsS1()
    {
        var result = MutateValid(document => Service(document, index: 0)["name"] = "App");

        AssertError(result, "applications[0].services[0].name must match ^[a-z][a-z0-9-]{0,30}$.");
    }

    [Test]
    public void Validate_WhenImageIsNotDigestPinned_ReportsS2()
    {
        var result = MutateValid(document => Service(document, index: 0)["image"] = "ghcr.io/example/sample:1.0.0");

        AssertError(result, "applications[0].services[0].image must be pinned as '<reference>@sha256:<64 hex digits>'.");
    }

    [Test]
    public void Validate_WhenImageTagIsLatest_ReportsS3()
    {
        var result = MutateValid(document => Service(document, index: 0)["imageTag"] = "latest");

        AssertError(result, "applications[0].services[0].imageTag is required and must not be 'latest'.");
    }

    [Test]
    public void Validate_WhenEntrypointIsAnEmptyArray_ReportsS4()
    {
        var result = MutateValid(document => Service(document, index: 0)["entrypoint"] = new JsonArray());

        AssertError(result, "applications[0].services[0].entrypoint must be null or a non-empty array of non-empty strings.");
    }

    [Test]
    public void Validate_WhenAnEnvironmentKeyIsNotAnIdentifier_ReportsS5()
    {
        var result = MutateValid(document => Service(document, index: 0)[EnvironmentKey]!["1BAD"] = "x");

        AssertError(result, "applications[0].services[0].environment key '1BAD' is not a valid environment-variable name.");
    }

    [Test]
    public void Validate_WhenAnEnvironmentValueReferencesAnUndeclaredVariable_ReportsS6()
    {
        var result = MutateValid(document => Service(document, index: 0)[EnvironmentKey]!["EXTRA"] = "${MISSING_VARIABLE}");

        AssertError(result, "applications[0].services[0].environment['EXTRA'] references undeclared variable '${MISSING_VARIABLE}'.");
    }

    [Test]
    public void Validate_WhenAUiPortTokenNamesAServiceWithoutAUiPort_ReportsS7()
    {
        var result = MutateValid(document =>
            Service(document, index: 0)[EnvironmentKey]!["SIDECAR_PORT"] = "${XE_UI_HOST_PORT_absent}");

        AssertError(result, "references '${XE_UI_HOST_PORT_absent}' but service 'absent' publishes no ui port.");
    }

    [Test]
    public void Validate_WhenASubstitutionTokenNeverCloses_ReportsS8()
    {
        var result = MutateValid(document => Service(document, index: 0)[EnvironmentKey]!["EXTRA"] = "${UNCLOSED");

        AssertError(result, "applications[0].services[0].environment['EXTRA'] contains a malformed '${' token.");
    }

    [Test]
    public void Validate_WhenContainerPortIsOutOfRange_ReportsS9()
    {
        var result = MutateValid(document => Service(document, index: 0)["ports"]!.AsArray()[0]!["containerPort"] = 0);

        AssertError(result, "applications[0].services[0].ports[0].containerPort must be between 1 and 65535.");
    }

    [Test]
    [Arguments("//evil.example/")]
    [Arguments("/settings\\admin")]
    [Arguments("/settings\u0001admin")]
    [Arguments("/settings\u007Fadmin")]
    [Arguments("settings")]
    public void Validate_WhenOpenPathIsUnsafe_ReportsS9(string openPath)
    {
        // "//host" is protocol-relative — the browser would leave the loopback origin the port was published on.
        // A backslash, a control character and DEL have no meaning in a local route and only exist to smuggle
        // something past whatever renders or logs the value; anything without a leading '/' is not a route at all.
        var result = MutateValid(document => Service(document, index: 0)["ports"]!.AsArray()[0]!["openPath"] = openPath);

        AssertError(result, "applications[0].services[0].ports[0].openPath must be an absolute path of at most 256 characters");
    }

    [Test]
    public void Validate_WhenOpenPathExceedsTheLengthCap_ReportsS9()
    {
        var tooLong = "/" + new string('a', ExternalAppCatalogValidator.MaxOpenPathLength);

        var result = MutateValid(document => Service(document, index: 0)["ports"]!.AsArray()[0]!["openPath"] = tooLong);

        AssertError(result, "applications[0].services[0].ports[0].openPath must be an absolute path of at most 256 characters");
    }

    [Test]
    public void Validate_WhenTwoFilesTargetTheSameContainerPath_ReportsS11()
    {
        // Both entries materialise at install; the one written last silently wins, so the installed file would depend
        // on array order rather than on anything the author declared.
        var result = MutateValid(document =>
        {
            var files = Service(document, index: 1)["files"]!.AsArray();
            files.Add(files[0]!.DeepClone());
        });

        AssertError(result, "applications[0].services[1].files[1].containerPath '/tmp/sidecar-settings.yml' is a duplicate.");
    }

    [Test]
    [Arguments("/app/./data")]
    [Arguments("/app//data")]
    [Arguments("/app/data2/")]
    public void Validate_WhenAStorageContainerPathIsNotCanonical_ReportsS10(string containerPath)
    {
        // Every one of these resolves to a path the manifest already declares or to one spelled twice. The duplicate
        // and nesting checks compare the strings, so an alias walks past both and two mounts land on one directory.
        var result = MutateValid(document => Service(document, index: 0)[StorageKey]!.AsArray()[1]!["containerPath"] = containerPath);

        AssertError(result, "applications[0].services[0].storage[1].containerPath must be an absolute POSIX path without '..', '.' or empty segments.");
    }

    [Test]
    public void Validate_WhenAFileContainerPathAliasesAStorageMount_ReportsS11()
    {
        // '/var/./cache/sidecar/settings.yml' IS inside the '/var/cache/sidecar' mount, but not by string prefix:
        // without the segment rule the file would be written into a volume the manifest never declared it in.
        var result = MutateValid(document =>
            Service(document, index: 1)["files"]!.AsArray()[0]!["containerPath"] = "/var/./cache/sidecar/settings.yml");

        AssertError(result, "applications[0].services[1].files[0].containerPath must be an absolute POSIX path without '..', '.' or empty segments.");
    }

    [Test]
    [Arguments("./files/sidecar/settings.yml")]
    [Arguments("files//sidecar/settings.yml")]
    [Arguments("files/sidecar/")]
    public void Validate_WhenAFileSourceIsNotCanonical_ReportsS11(string source)
    {
        var result = MutateValid(document => Service(document, index: 1)["files"]!.AsArray()[0]!["source"] = source);

        AssertError(result, "applications[0].services[1].files[0].source must be a relative POSIX path without '..', '.' or empty segments.");
    }

    [Test]
    public void Validate_WhenTwoFilesShareASource_ReportsS11()
    {
        // The installer materialises every entry at {instanceDir}/files/{service}/{source}: two entries sharing a
        // source collide on one host file, so the second containerPath silently receives the first entry's body.
        var result = MutateValid(document =>
        {
            var files = Service(document, index: 1)["files"]!.AsArray();
            var duplicate = files[0]!.DeepClone();
            duplicate["containerPath"] = "/tmp/sidecar-settings-copy.yml";
            files.Add(duplicate);
        });

        AssertError(result, "applications[0].services[1].files[1].source 'files/sidecar/settings.yml' is a duplicate.");
    }

    [Test]
    public void Validate_WhenTwoStorageMountsNest_ReportsS10()
    {
        var result = MutateValid(document =>
            Service(document, index: 0)[StorageKey]!.AsArray()[1]!["containerPath"] = "/app/data/logs");

        AssertError(result, "applications[0].services[0].storage[1].containerPath must not nest inside '/app/data'.");
    }

    [Test]
    public void Validate_WhenAFileBodyDoesNotMatchItsSha256_ReportsS11()
    {
        var result = MutateValid(document =>
            Service(document, index: 1)["files"]!.AsArray()[0]!["contentBase64"] = "b3RoZXI6IGZhbHNlCg==");

        AssertError(result, "applications[0].services[1].files[0].contentBase64 does not match the declared sha256.");
    }

    [Test]
    public void Validate_WhenHealthcheckIntervalIsOutOfRange_ReportsS12()
    {
        var result = MutateValid(document => Service(document, index: 0)[HealthcheckKey]!["intervalSeconds"] = 0);

        AssertError(result, "applications[0].services[0].healthcheck.intervalSeconds must be between 1 and 300.");
    }

    [Test]
    public void Validate_WhenDependsOnNamesAnUnknownServiceOrFormsACycle_ReportsS13()
    {
        var unknown = MutateValid(document =>
            Service(document, index: 0)["dependsOn"]!.AsArray()[0]!["service"] = "nowhere");
        AssertError(unknown, "applications[0].services[0].dependsOn[0].service 'nowhere' is not a declared service.");

        var cycle = MutateValid(document =>
        {
            Service(document, index: 0)["dependsOn"]!.AsArray()[0]!["condition"] = "started";
            Service(document, index: 1)["dependsOn"] = new JsonArray(
                new JsonObject { ["service"] = "app", ["condition"] = "started" });
        });
        AssertError(cycle, "applications[0].services[0] participates in a dependsOn cycle.");
    }

    [Test]
    public void Validate_WhenCapAddLeavesTheAllowList_ReportsS14()
    {
        var result = MutateValid(document => Service(document, index: 0)["capAdd"]!.AsArray().Add("SYS_ADMIN"));

        AssertError(result, "applications[0].services[0].capAdd contains 'SYS_ADMIN', which is not in the allow-list");
    }

    [Test]
    public void Validate_WhenExtraHostsNamesSomethingElse_ReportsS15()
    {
        var result = MutateValid(document => Service(document, index: 0)["extraHosts"] = new JsonArray("gateway:1.2.3.4"));

        AssertError(result, "applications[0].services[0].extraHosts contains 'gateway:1.2.3.4'; only 'host-gateway' is allowed.");
    }

    // ---------------------------------------------------------------- variable rules

    [Test]
    public void Validate_WhenVariableNameIsMalformed_ReportsV1()
    {
        var result = MutateValid(document => Variable(document, index: 1)["name"] = "1TWEAK");

        AssertError(result, "applications[0].variables[1].name must match ^[A-Za-z_][A-Za-z0-9_]*$ and be at most 64 characters.");
    }

    [Test]
    public void Validate_WhenVariableLabelIsBlank_ReportsV2()
    {
        var result = MutateValid(document => Variable(document, index: 1)["label"] = "");

        AssertError(result, "applications[0].variables[1].label is required and must be at most 80 characters.");
    }

    [Test]
    public void Validate_WhenVariableTypeIsUnknown_ReportsV3()
    {
        var result = MutateValid(document => Variable(document, index: 1)["type"] = "text");

        AssertError(result, "applications[0].variables[1].type must be one of string, secret, integer, boolean, enum.");
    }

    [Test]
    public void Validate_WhenASecretDeclaresADefault_ReportsV4()
    {
        var result = MutateValid(document => Variable(document, index: 0)[DefaultKey] = "hunter22");

        AssertError(result, "applications[0].variables[0].default must be null for a secret variable.");
    }

    [Test]
    public void Validate_WhenAnEnumDeclaresNoAllowedValues_ReportsV5()
    {
        var result = MutateValid(document => Variable(document, index: 1)[AllowedValuesKey] = null);

        AssertError(result, "applications[0].variables[1].allowedValues is required and must be distinct for an enum variable.");
    }

    [Test]
    public void Validate_WhenTheDefaultIsOutsideTheAllowedValues_ReportsV6()
    {
        var result = MutateValid(document => Variable(document, index: 1)[DefaultKey] = "medium");

        AssertError(result, "applications[0].variables[1].default does not satisfy the declared type/validation.");
    }

    [Test]
    public void Validate_WhenAnIntegerDefaultViolatesTheDeclaredValidation_ReportsV6()
    {
        // The type predicate alone accepts any parseable integer, so a default the install form would reject for its
        // length shipped as if it were valid.
        var result = MutateValid(document =>
        {
            var variable = Variable(document, index: 1);
            variable["type"] = "integer";
            variable[AllowedValuesKey] = null;
            variable[DefaultKey] = "123456";
            variable[ValidationKey] = new JsonObject { ["minLength"] = 1, ["maxLength"] = 3, ["pattern"] = null };
        });

        AssertError(result, "applications[0].variables[1].default does not satisfy the declared type/validation.");
    }

    [Test]
    public void Validate_WhenABooleanDefaultViolatesTheDeclaredValidation_ReportsV6()
    {
        var result = MutateValid(document =>
        {
            var variable = Variable(document, index: 1);
            variable["type"] = "boolean";
            variable[AllowedValuesKey] = null;
            variable[DefaultKey] = "true";
            variable[ValidationKey] = new JsonObject { ["minLength"] = 8, ["maxLength"] = null, ["pattern"] = null };
        });

        AssertError(result, "applications[0].variables[1].default does not satisfy the declared type/validation.");
    }

    [Test]
    public void Validate_WhenAnEnumDefaultViolatesTheDeclaredValidation_ReportsV6()
    {
        // 'fast' IS an allowed value; it still violates the pattern the same variable declares.
        var result = MutateValid(document =>
        {
            var variable = Variable(document, index: 1);
            variable[ValidationKey] = new JsonObject { ["minLength"] = null, ["maxLength"] = null, ["pattern"] = "^slow$" };
        });

        AssertError(result, "applications[0].variables[1].default does not satisfy the declared type/validation.");
    }

    [Test]
    public void Validate_WhenAnIntegerVariableDeclaresAPattern_ReportsV7()
    {
        var result = MutateValid(document =>
        {
            var variable = Variable(document, index: 1);
            variable["type"] = "integer";
            variable[AllowedValuesKey] = null;
            variable[DefaultKey] = "7";
            variable[ValidationKey] = new JsonObject
            {
                ["minLength"] = null,
                ["maxLength"] = null,
                ["pattern"] = "^[0-9]+$"
            };
        });

        AssertError(result, "applications[0].variables[1].validation.pattern is not valid for an integer variable.");
    }

    [Test]
    public void Validate_WhenAValidationPatternCannotCompileNonBacktracking_ReportsV8()
    {
        var malformed = MutateValid(document => Variable(document, index: 0)[ValidationKey]!["pattern"] = "(");
        AssertError(malformed, "applications[0].variables[0].validation.pattern must be a non-backtracking regular expression of at most 200 characters.");

        var lookbehind = MutateValid(document => Variable(document, index: 0)[ValidationKey]!["pattern"] = "(?<=a)b");
        AssertError(lookbehind, "applications[0].variables[0].validation.pattern must be a non-backtracking regular expression of at most 200 characters.");
    }

    [Test]
    public void Validate_WhenADeclaredVariableIsNeverReferenced_ReportsV9()
    {
        var result = MutateValid(document => Application(document)[VariablesKey]!.AsArray().Add(new JsonObject
        {
            ["name"] = "SAMPLE_UNUSED",
            ["label"] = "Unused",
            ["description"] = null,
            ["type"] = "string",
            ["required"] = false,
            [DefaultKey] = null,
            [AllowedValuesKey] = null,
            [ValidationKey] = null,
            ["advanced"] = true
        }));

        AssertError(result, "applications[0].variables[2].name 'SAMPLE_UNUSED' is declared but never referenced by any service environment value.");
    }

    [Test]
    public void Validate_WhenAVariableUsesTheReservedPrefix_ReportsV10()
    {
        var result = MutateValid(document => Variable(document, index: 1)["name"] = "XE_CUSTOM");

        AssertError(result, "applications[0].variables[1].name must not start with the reserved prefix XE_.");
    }

    // ---------------------------------------------------------------- Codex round 2

    [Test]
    [Arguments("files/sidecar/settings.yml/nested.yml", "must not nest inside 'files/sidecar/settings.yml'")]
    [Arguments("files/sidecar", "must not contain 'files/sidecar/settings.yml'")]
    public void Validate_WhenTwoFileSourcesNest_ReportsS11(string source, string expected)
    {
        // Both entries are regular files materialised under {instanceDir}/files/{service}/: 'a' and 'a/b' cannot both
        // exist, so the second write either fails or turns the first file into a directory. Equality alone let the
        // pair through in either order.
        var result = MutateValid(document =>
        {
            var files = Service(document, index: 1)["files"]!.AsArray();
            var second = files[0]!.DeepClone();
            second["source"] = source;
            second["containerPath"] = "/tmp/sidecar-settings-nested.yml";
            files.Add(second);
        });

        AssertError(result, $"applications[0].services[1].files[1].source {expected}.");
    }

    [Test]
    [Arguments("/tmp/sidecar-settings.yml/nested.yml", "must not nest inside '/tmp/sidecar-settings.yml'")]
    [Arguments("/tmp", "must not contain '/tmp/sidecar-settings.yml'")]
    public void Validate_WhenTwoFileContainerPathsNest_ReportsS11(string containerPath, string expected)
    {
        // Same collision on the container side: mounting a file at '/tmp/x' and another at '/tmp/x/y' cannot both
        // succeed, and the engine would only find out when the container refuses to start.
        var result = MutateValid(document =>
        {
            var files = Service(document, index: 1)["files"]!.AsArray();
            var second = files[0]!.DeepClone();
            second["source"] = "files/sidecar/nested.yml";
            second["containerPath"] = containerPath;
            files.Add(second);
        });

        AssertError(result, $"applications[0].services[1].files[1].containerPath {expected}.");
    }

    [Test]
    public void Validate_WhenAnApplicationIdEndsWithALineFeed_ReportsA1()
    {
        // .NET's '$' also matches immediately before a terminal '\n', so an unanchored-at-the-end pattern accepts a
        // value carrying a trailing newline. Every whole-string validator here uses \A…\z for that reason.
        var result = MutateValid(document => Application(document)["id"] = "sample-app\n");

        AssertError(result, "applications[0].id must match ^[a-z][a-z0-9-]{1,40}$.");
    }

    [Test]
    public void Validate_WhenAServiceNameEndsWithALineFeed_ReportsS1()
    {
        var result = MutateValid(document => Service(document, index: 1)["name"] = "sidecar\n");

        AssertError(result, "applications[0].services[1].name must match ^[a-z][a-z0-9-]{0,30}$.");
    }

    [Test]
    public void Validate_WhenAVariableNameEndsWithALineFeed_ReportsV1()
    {
        var result = MutateValid(document => Variable(document, index: 1)["name"] = "SAMPLE_TWEAK\n");

        AssertError(result, "applications[0].variables[1].name must match ^[A-Za-z_][A-Za-z0-9_]*$ and be at most 64 characters.");
    }

    [Test]
    public void Validate_WhenAnEnvironmentKeyEndsWithALineFeed_ReportsS5()
    {
        var result = MutateValid(document =>
        {
            var environment = Service(document, index: 1)[EnvironmentKey]!.AsObject();
            environment.Remove("SIDECAR_HOME");
            environment["SIDECAR_HOME\n"] = "/var/lib/sidecar";
        });

        AssertError(result, "is not a valid environment-variable name.");
    }

    [Test]
    public void Validate_WhenAPinnedImageEndsWithALineFeed_ReportsS2()
    {
        var result = MutateValid(document => Service(document, index: 1)["image"] =
            "docker.io/example/sidecar@sha256:2f0c0d6bd3ad19a51a5d0c1b2e3f4a5b6c7d8e9f00112233445566778899aabb\n");

        AssertError(result, "applications[0].services[1].image must be pinned as '<reference>@sha256:<64 hex digits>'.");
    }

    [Test]
    public void Validate_WhenAFileSha256EndsWithALineFeed_ReportsS11()
    {
        var result = MutateValid(document => Service(document, index: 1)["files"]!.AsArray()[0]!["sha256"] =
            "778fb2bf3e6ae22f6cf14f374333b24961f9523cf1980560e5e49318d976f334\n");

        AssertError(result, "applications[0].services[1].files[0].sha256 must be 64 lowercase hex digits.");
    }

    [Test]
    public void Validate_WhenAFileBodyIsNullOrAbsent_ReportsS11()
    {
        // The contract types contentBase64 as a non-nullable string, so both spellings reach the validator as null.
        // Coercing them to "" would have matched the sha256 of zero bytes and shipped a null body downstream.
        var nullLiteral = MutateValid(document =>
            Service(document, index: 1)["files"]!.AsArray()[0]!["contentBase64"] = null);
        AssertError(nullLiteral, "applications[0].services[1].files[0].contentBase64 is required");

        var absent = MutateValid(document =>
            Service(document, index: 1)["files"]!.AsArray()[0]!.AsObject().Remove("contentBase64"));
        AssertError(absent, "applications[0].services[1].files[0].contentBase64 is required");
    }

    [Test]
    public void Validate_WhenAFileBodyIsTheEmptyString_Accepts()
    {
        // An empty file stays legal: "" decodes to zero bytes and must match the sha256 of zero bytes.
        var result = MutateValid(document =>
        {
            var file = Service(document, index: 1)["files"]!.AsArray()[0]!;
            file["contentBase64"] = "";
            file["sha256"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        });

        AssertEx.True(result.IsValid, string.Join(" | ", result.Errors));
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertError(ExternalAppCatalogValidationResult result, string expected)
    {
        AssertEx.False(result.IsValid, $"expected a rejection but the document validated; expected error '{expected}'");
        AssertEx.Contains(
            result.Errors,
            error => error.Contains(expected, StringComparison.Ordinal),
            $"expected an error containing '{expected}' but got: {string.Join(" | ", result.Errors)}");
    }

    private static string BuildValidJson()
    {
        var document = ParseValid();
        Refingerprint(document);
        return document.ToJsonString(SerializerOptions);
    }

    private static ExternalAppCatalogValidationResult MutateValid(Action<JsonObject> mutate)
    {
        var document = ParseValid();
        mutate(document);
        Refingerprint(document);
        return ExternalAppCatalogValidator.Validate(document.ToJsonString(SerializerOptions));
    }

    private static ExternalAppCatalogValidationResult MutateValidWithoutRefingerprinting(Action<JsonObject> mutate)
    {
        var document = ParseValid();
        Refingerprint(document);
        mutate(document);
        return ExternalAppCatalogValidator.Validate(document.ToJsonString(SerializerOptions));
    }

    private static JsonObject ParseValid()
    {
        return JsonNode.Parse(ValidDocumentJson)!.AsObject();
    }

    /// <summary>
    ///     Restores <c>manifestSha256</c> after a mutation so every rule test asserts its own rule rather than also
    ///     tripping the fingerprint rule. The value comes from the production fingerprint over the mutated manifest,
    ///     so the helper cannot mask a canonicalisation defect — the two dedicated A21 tests bypass it.
    /// </summary>
    private static void Refingerprint(JsonObject document)
    {
        if (document[ApplicationsKey] is not JsonArray applications)
        {
            return;
        }

        foreach (var node in applications)
        {
            if (node is not JsonObject application)
            {
                continue;
            }

            ApplicationManifest? manifest;
            try
            {
                manifest = application.Deserialize<ApplicationManifest>(SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is not null)
            {
                application[ExternalAppManifestFingerprint.HashPropertyName] =
                    ExternalAppManifestFingerprint.Compute(manifest);
            }
        }
    }

    private static JsonObject Application(JsonObject document)
    {
        return document[ApplicationsKey]!.AsArray()[0]!.AsObject();
    }

    private static JsonObject Service(JsonObject document, int index)
    {
        return Application(document)[ServicesKey]!.AsArray()[index]!.AsObject();
    }

    private static JsonObject Variable(JsonObject document, int index)
    {
        return Application(document)[VariablesKey]!.AsArray()[index]!.AsObject();
    }
}
