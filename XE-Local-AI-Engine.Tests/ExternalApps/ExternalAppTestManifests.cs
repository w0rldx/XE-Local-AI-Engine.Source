namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Hand-built manifests for the runtime tests. Deliberately not the seed catalog: these suites need a manifest
///     shaped around the one rule under test — two services declaring the same storage name, a token naming a service
///     with no port, an asset whose recorded hash is wrong — and the seed cannot be bent into those shapes without
///     recomputing its fingerprint every time.
/// </summary>
internal static class ExternalAppTestManifests
{
    /// <summary>A digest-pinned image reference the catalog validator's own regex accepts.</summary>
    public const string Image = "ghcr.io/example/app@sha256:0000000000000000000000000000000000000000000000000000000000000001";

    /// <summary>A second one, for the tests that care that two services do not share an image.</summary>
    public const string SecondImage = "ghcr.io/example/sidecar@sha256:0000000000000000000000000000000000000000000000000000000000000002";

    public static ApplicationManifest Manifest(IReadOnlyList<ApplicationService> services,
        IReadOnlyList<ApplicationVariable>? variables = null,
        ApplicationPermissions? permissions = null,
        ApplicationResources? resources = null,
        IReadOnlyList<string>? requires = null,
        int manifestVersion = 1)
    {
        return new ApplicationManifest("test-app",
            manifestVersion,
            "0000000000000000000000000000000000000000000000000000000000000000",
            "Test App",
            "A test application.",
            "A test application used by the External Apps runtime tests.",
            "https://example.invalid",
            "MIT",
            "firstParty",
            "1.0.0",
            requires ?? ["containers", "networks", "bindStorage"],
            permissions ?? new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none"),
            resources ?? new ApplicationResources(MinimumMemoryMb: 512, RecommendedMemoryMb: 1024, CpuHint: 1, PidsLimit: 512),
            services,
            variables ?? []);
    }

    public static ApplicationService Service(string name,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<ApplicationPort>? ports = null,
        IReadOnlyList<ApplicationStorage>? storage = null,
        IReadOnlyList<ApplicationFile>? files = null,
        IReadOnlyList<ApplicationDependency>? dependsOn = null,
        IReadOnlyList<string>? capAdd = null,
        IReadOnlyList<string>? extraHosts = null,
        bool readOnlyRootFilesystem = false,
        ApplicationHealthcheck? healthcheck = null,
        string image = Image)
    {
        return new ApplicationService(name,
            image,
            "1.0.0",
            Entrypoint: null,
            Command: null,
            environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ports ?? [],
            storage ?? [],
            files ?? [],
            healthcheck,
            dependsOn ?? [],
            capAdd ?? [],
            extraHosts ?? [],
            readOnlyRootFilesystem);
    }

    /// <summary>A <c>ui</c> port declaration.</summary>
    public static ApplicationPort UiPort(int containerPort, int? preferredHostPort = null, string? openPath = null)
    {
        return new ApplicationPort(containerPort, "ui", preferredHostPort, openPath);
    }

    /// <summary>A catalog asset whose recorded hash is the hash of <paramref name="content" />, as the validator requires.</summary>
    public static ApplicationFile File(string source, string containerPath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ApplicationFile(source,
            containerPath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Convert.ToBase64String(bytes));
    }

    /// <summary>A declared variable; <paramref name="type" /> is <c>string</c> unless a test needs <c>secret</c>.</summary>
    public static ApplicationVariable Variable(string name,
        bool required = false,
        string? @default = null,
        string type = "string")
    {
        return new ApplicationVariable(name, name, Description: null, type, required, @default, AllowedValues: null, Validation: null, Advanced: false);
    }
}
