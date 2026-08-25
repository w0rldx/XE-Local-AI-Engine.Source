namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Request-shape rules that belong to the contract itself rather than to any one provider. Validating
///     <see cref="SandboxCreateRequest.MaxJailDiskBytes" /> at the init accessor is what makes a nonsense ceiling
///     impossible to construct — a provider-side check would leave every other provider free to accept it, and a zero
///     would read as "unlimited" exactly where the field means the opposite.
/// </summary>
public sealed class SandboxCreateRequestTests
{
    [Test]
    public void MaxJailDiskBytes_DefaultsToNull_MeaningTheNodeWideCeiling()
    {
        AssertEx.Null(Request().MaxJailDiskBytes,
            "an unset per-sandbox ceiling must stay null so the provider applies the node-wide one");
    }

    [Test]
    public void MaxJailDiskBytes_WithAPositiveValue_IsCarriedThrough()
    {
        var request = Request() with
        {
            MaxJailDiskBytes = 4L * 1024 * 1024
        };

        AssertEx.Equal(expected: 4L * 1024 * 1024, request.MaxJailDiskBytes.Value);
    }

    [Test]
    public void MaxJailDiskBytes_WhenZero_IsRejected()
    {
        // Zero is not "no ceiling" here: this field can only tighten the node-wide one, so accepting it would either
        // disable a control the caller was asking to strengthen or terminate every command instantly.
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = Request() with
        {
            MaxJailDiskBytes = 0
        });
    }

    [Test]
    public void MaxJailDiskBytes_WhenNegative_IsRejected()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = Request() with
        {
            MaxJailDiskBytes = -1
        });
    }

    private static SandboxCreateRequest Request()
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner-1",
                NodeId = "node-1",
                ProviderName = "process",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = 1
            },
            RuntimeProfile = "dotnet-agent-home"
        };
    }
}
