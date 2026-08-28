namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompatible;

using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The normalized base address is the value the connect probe, the chat transport and the outbound endpoint guard
///     all share. If normalization were not a fixed point — or admitted a shape the guard cannot pin — the guard would
///     either reject legitimate traffic or admit a destination the operator never reviewed.
/// </summary>
public sealed class OpenAICompatibleBaseAddressTests
{
    [Test]
    [Arguments("http://127.0.0.1:8080", "http://127.0.0.1:8080/v1/")]
    [Arguments("http://127.0.0.1:8080/", "http://127.0.0.1:8080/v1/")]
    [Arguments("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/")]
    [Arguments("http://127.0.0.1:8080/v1/", "http://127.0.0.1:8080/v1/")]
    [Arguments("https://api.example.com", "https://api.example.com/v1/")]
    // A gateway prefix is preserved and /v1 appended beneath it, not in place of it.
    [Arguments("https://gw.example.com/openai", "https://gw.example.com/openai/v1/")]
    [Arguments("  https://api.example.com/v1  ", "https://api.example.com/v1/")]
    public void TryNormalize_CanonicalizesToAV1TerminatedBase(string input, string expected)
    {
        AssertEx.True(OpenAICompatibleBaseAddress.TryNormalize(input, out var normalized));
        AssertEx.Equal(expected, AssertEx.NotNull(normalized).AbsoluteUri);
    }

    [Test]
    public void TryNormalize_IsAFixedPoint()
    {
        // Re-normalizing a stored value must never append a second /v1 — the store round-trips this value on every save.
        AssertEx.True(OpenAICompatibleBaseAddress.TryNormalize("http://host:9000", out var once));
        AssertEx.True(OpenAICompatibleBaseAddress.TryNormalize(once, out var twice));
        AssertEx.Equal(AssertEx.NotNull(once).AbsoluteUri, AssertEx.NotNull(twice).AbsoluteUri);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("/relative/v1")]
    [Arguments("file:///etc/passwd")]
    [Arguments("ws://host/v1")]
    // Credentials belong in the encrypted key field, never in a base URL that gets logged and rendered.
    [Arguments("https://user:secret@api.example.com/v1")]
    [Arguments("https://api.example.com/v1?key=abc")]
    [Arguments("https://api.example.com/v1#frag")]
    public void TryNormalize_RejectsUnusableOrUnsafeEndpoints(string? input)
    {
        AssertEx.False(OpenAICompatibleBaseAddress.TryNormalize(input, out _));
    }

    [Test]
    public void Normalize_ThrowsForARejectedEndpoint()
    {
        _ = AssertEx.Throws<ArgumentException>(() => OpenAICompatibleBaseAddress.Normalize(new Uri("ftp://host/v1")));
    }
}
