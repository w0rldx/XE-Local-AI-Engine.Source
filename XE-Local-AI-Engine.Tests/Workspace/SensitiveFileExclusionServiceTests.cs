namespace XE_Local_AI_Engine.Tests.Workspace;

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
        AssertEx.True(_service.IsExcluded(name, false), $"{name} must be excluded");
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
        AssertEx.True(_service.IsExcluded(name, true), $"{name} directory must be excluded");
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
        AssertEx.True(_service.IsExcluded(".ENV", false), ".ENV must be excluded");
        AssertEx.True(_service.IsExcluded("Node_Modules", true), "Node_Modules must be excluded");
    }
}
