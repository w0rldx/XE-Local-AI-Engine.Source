namespace XE_Local_AI_Engine.Tests.Workspace;

using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Sensitive-file exclusion coverage: secrets, the host <c>.git</c> baseline, credential bundles, and
///     generated/heavy output directories are excluded; ordinary source files are not.
/// </summary>
public sealed class SensitiveFileExclusionServiceTests
{
    private readonly SensitiveFileExclusionService _service = new();

    [Test]
    [Arguments(".env")]
    [Arguments(".env.local")]
    [Arguments(".env.Production")]
    [Arguments("secrets.json")]
    [Arguments("appsettings.Production.json")]
    [Arguments("cloud-credentials.enc")]
    [Arguments("worker-credentials.enc")]
    [Arguments("my-credentials.enc")]
    public void IsExcluded_WhenSecretFile_ReturnsTrue(string name)
    {
        AssertEx.True(_service.IsExcluded(name, isDirectory: false), $"{name} must be excluded");
    }

    [Test]
    [Arguments(".git")]
    [Arguments(".ssh")]
    [Arguments("bin")]
    [Arguments("obj")]
    [Arguments("node_modules")]
    [Arguments("dist")]
    [Arguments("coverage")]
    public void IsExcluded_WhenGeneratedOrSecretDirectory_ReturnsTrue(string name)
    {
        AssertEx.True(_service.IsExcluded(name, isDirectory: true), $"{name} directory must be excluded");
    }

    [Test]
    [Arguments("Program.cs", false)]
    [Arguments("README.md", false)]
    [Arguments("appsettings.json", false)]
    [Arguments("src", true)]
    [Arguments("tests", true)]
    public void IsExcluded_WhenOrdinaryEntry_ReturnsFalse(string name, bool isDirectory)
    {
        AssertEx.False(_service.IsExcluded(name, isDirectory), $"{name} must not be excluded");
    }

    [Test]
    public void IsExcluded_IsCaseInsensitive()
    {
        AssertEx.True(_service.IsExcluded(".ENV", isDirectory: false), ".ENV must be excluded");
        AssertEx.True(_service.IsExcluded("Node_Modules", isDirectory: true), "Node_Modules must be excluded");
    }

    /// <summary>
    ///     The product's own at-rest secrets. <c>node.key</c> is the highest-value file the node ever writes: it is the
    ///     32-byte operator secret from which the SQLite column key, the node JWT signing key and the Data Protection
    ///     key-ring KEK are all derived, so leaking it decrypts every <c>.enc</c> blob beside it and forges node JWTs.
    ///     <c>node.sqlite</c> and its sidecars are the database those keys protect.
    /// </summary>
    [Test]
    [Arguments("node.key")]
    [Arguments("node.sqlite")]
    [Arguments("node.sqlite-wal")]
    [Arguments("node.sqlite-shm")]
    [Arguments("hf-token.enc")]
    [Arguments("github-token.enc")]
    [Arguments("codex-oauth-tokens.enc")]
    [Arguments("entra-authcode-account.enc")]
    [Arguments("entra-auth-record.enc")]
    public void IsExcluded_WhenProductWrittenSecret_ReturnsTrue(string name)
    {
        AssertEx.True(_service.IsExcluded(name, isDirectory: false), $"{name} must be excluded");
    }

    /// <summary>
    ///     Credential stores a developer's home directory and repositories routinely hold. These are not written by this
    ///     product, but a selected folder or a registered repository is frequently a developer's own working tree.
    /// </summary>
    [Test]
    [Arguments(".netrc", false)]
    [Arguments(".npmrc", false)]
    [Arguments(".git-credentials", false)]
    [Arguments("server.pem", false)]
    [Arguments("client.pfx", false)]
    [Arguments("bundle.p12", false)]
    [Arguments("id_rsa", false)]
    [Arguments("id_rsa.pub", false)]
    [Arguments("id_ed25519", false)]
    [Arguments(".aws", true)]
    [Arguments(".kube", true)]
    [Arguments(".docker", true)]
    public void IsExcluded_WhenDeveloperCredentialStore_ReturnsTrue(string name, bool isDirectory)
    {
        AssertEx.True(_service.IsExcluded(name, isDirectory), $"{name} must be excluded");
    }

    /// <summary>
    ///     <see cref="ISensitiveFileExclusionService.ExcludedEntryNames" /> is the authoritative
    ///     <c>find -name</c> / <c>grep --exclude</c> flag set, and <see cref="ISensitiveFileExclusionService.IsExcluded" />
    ///     is the in-process post-filter behind it. A name present in one but not the other is a silent half-landing:
    ///     the flag advertises protection the filter does not enforce. This walks every advertised pattern, materializes
    ///     a concrete file name from it, and requires the filter to agree.
    /// </summary>
    [Test]
    public void ExcludedEntryNames_AndIsExcluded_AgreeOnEveryAdvertisedPattern()
    {
        foreach (var pattern in _service.ExcludedEntryNames)
        {
            // Every advertised pattern is a literal name or a single-wildcard glob, so replacing '*' with an ordinary
            // token yields a name that pattern is claiming to cover.
            var concrete = pattern.Replace("*", "sample", StringComparison.Ordinal);
            AssertEx.True(_service.IsExcluded(concrete, isDirectory: false),
                $"'{pattern}' is advertised as an exclusion flag but IsExcluded('{concrete}') does not enforce it");
        }
    }

    /// <summary>
    ///     Generated output is skipped by the workspace COPY but is NOT a secret, so a read path gating on
    ///     <see cref="ISensitiveFileExclusionService.IsSecret" /> must let it through. Reading
    ///     <c>obj/project.assets.json</c> after a failed restore is a primary reason Development Mode exists; refusing
    ///     it protects nothing, because build output holds no credential.
    /// </summary>
    [Test]
    [Arguments("bin")]
    [Arguments("obj")]
    [Arguments("node_modules")]
    [Arguments("dist")]
    [Arguments("coverage")]
    [Arguments(".vs")]
    [Arguments(".idea")]
    [Arguments(".git")]
    public void IsSecret_WhenCopySkippedBuildOutput_ReturnsFalseEvenThoughIsExcludedIsTrue(string name)
    {
        AssertEx.True(_service.IsExcluded(name, isDirectory: true), $"{name} must still be skipped by the copy filter");
        AssertEx.False(_service.IsSecret(name), $"{name} is generated output, not a credential, and must stay readable");
    }

    /// <summary>
    ///     Everything credential-bearing must answer true to BOTH predicates: secrets are a strict subset of the copy
    ///     filter, so splitting the two sets must not have dropped anything out of the narrower one.
    /// </summary>
    [Test]
    [Arguments("node.key")]
    [Arguments("node.sqlite")]
    [Arguments(".env")]
    [Arguments(".env.local")]
    [Arguments("secrets.json")]
    [Arguments("appsettings.Production.json")]
    [Arguments("hf-token.enc")]
    [Arguments("codex-oauth-tokens.enc")]
    [Arguments(".ssh")]
    [Arguments(".aws")]
    [Arguments(".netrc")]
    [Arguments(".npmrc")]
    [Arguments(".git-credentials")]
    [Arguments("server.pem")]
    [Arguments("id_rsa")]
    public void IsSecret_WhenCredentialBearing_ReturnsTrueAndStaysASubsetOfIsExcluded(string name)
    {
        AssertEx.True(_service.IsSecret(name), $"{name} must be treated as a secret");
        AssertEx.True(_service.IsExcluded(name, isDirectory: false), $"{name} must also remain excluded from the copy");
    }

    /// <summary>
    ///     The same drift guard as <see cref="ExcludedEntryNames_AndIsExcluded_AgreeOnEveryAdvertisedPattern" />, for
    ///     the narrower flag set the read paths pass to grep. An entry advertised here that
    ///     <see cref="ISensitiveFileExclusionService.IsSecret" /> does not enforce is a silent half-landing.
    /// </summary>
    [Test]
    public void SecretEntryNames_AndIsSecret_AgreeOnEveryAdvertisedPattern()
    {
        foreach (var pattern in _service.SecretEntryNames)
        {
            var concrete = pattern.Replace("*", "sample", StringComparison.Ordinal);
            AssertEx.True(_service.IsSecret(concrete),
                $"'{pattern}' is advertised as a secret exclusion flag but IsSecret('{concrete}') does not enforce it");

            // The secret flag set must stay a strict subset of the copy filter's flag set.
            AssertEx.Contains(_service.ExcludedEntryNames, pattern,
                $"'{pattern}' is advertised as a secret but is missing from the copy filter's flag set");
        }
    }

    /// <summary>
    ///     The widened set must not start swallowing ordinary repository content — an exclusion that hides source files
    ///     is a capability regression for every agent that reads a workspace.
    /// </summary>
    [Test]
    [Arguments("keychain.cs", false)]
    [Arguments("Encoder.cs", false)]
    [Arguments("node.ts", false)]
    [Arguments("package.json", false)]
    [Arguments("appsettings.Development.json", false)]
    [Arguments("id_generator.py", false)]
    [Arguments("docs", true)]
    public void IsExcluded_WhenOrdinaryEntryResemblesASecret_ReturnsFalse(string name, bool isDirectory)
    {
        AssertEx.False(_service.IsExcluded(name, isDirectory), $"{name} must not be excluded");
    }
}
