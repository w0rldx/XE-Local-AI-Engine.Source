namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The namespaced external-model identity is the routing key AND a validation boundary: it is parsed on the chat
///     path, written into the provider map (case-insensitive) and into the tool-capable allow-list (ordinal), so its
///     grammar and its ONE canonical spelling are what keep those stores agreeing. These tests pin both.
/// </summary>
public sealed class ExternalModelIdTests
{
    [Test]
    [Arguments("conn", "model", "ext:conn/model")]
    [Arguments("unsloth-box", "unsloth/Qwen3.8-27B-GGUF", "ext:unsloth-box/unsloth/Qwen3.8-27B-GGUF")]
    [Arguments("c1", "llama3:8b", "ext:c1/llama3:8b")]
    // The connection slug is lowered; the WIRE id is preserved byte-for-byte, because it is what the remote server
    // matches on and remote model ids are case-sensitive.
    [Arguments("MyBox", "Org/Model_V2.1", "ext:mybox/Org/Model_V2.1")]
    public void Format_BuildsTheCanonicalNamespacedId(string connectionId, string wireId, string expected)
    {
        AssertEx.Equal(expected, ExternalModelId.Format(connectionId, wireId));
    }

    [Test]
    [Arguments("conn_with_underscore")]
    [Arguments("has space")]
    [Arguments("")]
    [Arguments("thisconnectionslugisdeliberatelylongerthanthirtytwo")]
    public void Format_WhenConnectionIdViolatesTheSlugGrammar_Throws(string connectionId)
    {
        _ = AssertEx.Throws<ArgumentException>(() => ExternalModelId.Format(connectionId, "model"));
    }

    [Test]
    [Arguments("../secret")]
    [Arguments("a//b")]
    [Arguments("/leading")]
    [Arguments("trailing/")]
    [Arguments("back\\slash")]
    [Arguments("with space")]
    [Arguments("")]
    public void Format_WhenWireIdIsUnsafe_Throws(string wireId)
    {
        _ = AssertEx.Throws<ArgumentException>(() => ExternalModelId.Format("conn", wireId));
    }

    [Test]
    public void TryParse_RoundTripsBothParts()
    {
        AssertEx.True(ExternalModelId.TryParse("ext:box/org/model:Q4_K_M", out var connectionId, out var wireId));
        AssertEx.Equal("box", connectionId);
        AssertEx.Equal("org/model:Q4_K_M", wireId);
    }

    [Test]
    [Arguments(null)]
    [Arguments("llama3:8b")]
    [Arguments("hf.co/org/repo:Q8_0")]
    [Arguments("ext:")]
    [Arguments("ext:box")]
    [Arguments("ext:/model")]
    [Arguments("ext:box/")]
    [Arguments("ext:bad_conn/model")]
    [Arguments("ext:box/../etc/passwd")]
    [Arguments("ext:box/http://evil.example/model")]
    public void TryParse_RejectsAnythingThatIsNotAWellFormedExternalId(string? modelName)
    {
        AssertEx.False(ExternalModelId.TryParse(modelName, out _, out _));
        AssertEx.Null(ExternalModelId.Canonicalize(modelName));
    }

    [Test]
    public void TryParse_RejectsAnIdLongerThanTheExternalBound()
    {
        // 165 = "ext:" + a 32-char slug + "/" + a 128-char wire id. One character more must fail, or the raised bound
        // would be a hole in the general model-name length guard rather than a scoped widening of it.
        var connectionId = new string(c: 'a', count: ExternalModelId.MaxConnectionIdLength);
        var atLimit = ExternalModelId.Format(connectionId, new string(c: 'b', count: ExternalModelId.MaxWireIdLength));

        AssertEx.Equal(ExternalModelId.MaxLength, atLimit.Length);
        AssertEx.True(ExternalModelId.TryParse(atLimit, out _, out _));
        AssertEx.False(ExternalModelId.TryParse(atLimit + "b", out _, out _));
    }

    [Test]
    public void Canonicalize_LowersTheConnectionSlugAndIsIdempotent()
    {
        var canonical = ExternalModelId.Canonicalize("ext:MyBox/Org/Model");

        AssertEx.Equal("ext:mybox/Org/Model", canonical);
        AssertEx.Equal(AssertEx.NotNull(canonical), ExternalModelId.Canonicalize(canonical));
    }

    [Test]
    [Arguments("ext:box/model", true)]
    [Arguments("ext:anything-at-all", true)]
    [Arguments("EXT:box/model", false)]
    [Arguments("llama3:8b", false)]
    [Arguments(null, false)]
    public void HasExternalScheme_IsAnOrdinalSchemeInspectionOnly(string? modelName, bool expected)
    {
        // Deliberately NOT a full parse: policy sites use it as a cheap pre-filter before paying for a registry lookup,
        // and it is ordinal so an "EXT:" spelling can never sneak past a case-insensitive comparison elsewhere.
        AssertEx.Equal(expected, ExternalModelId.HasExternalScheme(modelName));
    }
}
