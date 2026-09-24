namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared secret scanner, graded directly rather than through a caller: its live callers are
///     <c>MemoryExtractionService</c> and <c>DevelopmentArtifactSanitizer</c>, so its dispositions are a
///     persistence-side control despite the AgentHome name.
/// </summary>
/// <remarks>
///     Three dispositions: a secret whose structural context IS the secret (PEM block, service-account JSON)
///     rejects the whole record; a secret in the metadata or the evidence paths rejects it; a secret in the content
///     is redacted in place and the record survives.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class MemoryProposalSecretScannerTests
{
    [Test]
    public void Scan_WhenContentCarriesAPemPrivateKey_RejectsTheRecord()
    {
        var result = ScanContent("key: -----BEGIN RSA PRIVATE KEY-----\nMIIE...");

        AssertEx.True(result.ShouldReject, "a PEM private-key block cannot be redacted — the block itself is the secret");
        AssertEx.Contains(result.RejectionReason, "private-key");
    }

    [Test]
    public void Scan_WhenContentCarriesAGoogleServiceAccountJson_RejectsTheRecord()
    {
        var result = ScanContent("""{"type": "service_account", "private_key": "secret"}""");

        AssertEx.True(result.ShouldReject, "service-account JSON is rejected, not redacted");
    }

    [Test]
    public void Scan_WhenAnEvidencePathCarriesASecret_RejectsTheRecord()
    {
        // Metadata and evidence are never redacted: a path that carries a credential rejects the whole record.
        var result = MemoryProposalSecretScanner.Scan(type: "node_memory_proposal",
            operation: "add",
            content: "Normal content.",
            evidence: ["/agent-home/workspace/selected/repo-01/config?api_key=AKIA1234567890ABCDEF"],
            confidence: "low");

        AssertEx.True(result.ShouldReject, "a secret in an evidence path must reject the whole record");
    }

    [Test]
    [Arguments("Token: ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890AB", "[REDACTED:github-token]")]
    [Arguments("Access key: AKIAIOSFODNN7EXAMPLE123", "[REDACTED:aws-access-key]")]
    [Arguments("Token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c was used.", "[REDACTED:jwt]")]
    [Arguments("Slack token: xoxb-123456789012-abcdefghijklmnop", "[REDACTED:slack-token]")]
    [Arguments(
        "Config: DefaultEndpointsProtocol=https;AccountName=devstore;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;EndpointSuffix=core.windows.net",
        "[REDACTED:azure-connection-string]")]
    public void Scan_WhenContentCarriesARedactableToken_RedactsItAndKeepsTheRecord(string content, string expectedMarker)
    {
        var result = ScanContent(content);

        AssertEx.False(result.ShouldReject, "a redactable token leaves the record usable");
        AssertEx.Contains(AssertEx.NotNull(result.RedactedContent, "the content must have been rewritten"), expectedMarker);
    }

    [Test]
    public void Scan_WhenAHighEntropyBearerSitsPastTheStart_RedactsTheTokenAndKeepsTheKeyword()
    {
        // Regression for RedactHighEntropyBearer: the keyword-length slice used the ABSOLUTE capture-group index
        // against the matched substring, so a match past offset 0 leaked token bytes or threw out of range.
        const string token = "aB3xK9mP2qR7sT4vW8yZ1cD5fG6hJ0kL"; // 32 chars, Shannon entropy 5.0 (>= 4.5).

        var result = ScanContent($"The deploy script set the header to Bearer {token} before calling the API.");

        AssertEx.False(result.ShouldReject, "a redactable token must not reject the record");
        var content = AssertEx.NotNull(result.RedactedContent, "the token must have been redacted");
        AssertEx.Contains(content, "[REDACTED:high-entropy-token]");
        AssertEx.Contains(content, "Bearer ", StringComparison.Ordinal, "the keyword prefix must be preserved");
        AssertEx.False(content.Contains(token, StringComparison.Ordinal), "ZERO token bytes may survive");
        // No leading fragment of the token may survive either (the buggy slice leaked the token's first bytes).
        AssertEx.False(content.Contains(token[..8], StringComparison.Ordinal), "no leading fragment of the token may survive");
    }

    [Test]
    public void Scan_WhenAHighEntropyTokenCarriesNoKeyword_TheFallbackStillRedactsIt()
    {
        const string token = "Zk7Qw2Np5Rt8Yx1Cv4Bm9Df6Gh3Jl0Ks"; // 33 chars, entropy 5.0.

        var result = ScanContent($"The value was {token} in the config.");

        var content = AssertEx.NotNull(result.RedactedContent, "the bare token must have been redacted");
        AssertEx.Contains(content, "[REDACTED:high-entropy-token]");
        AssertEx.False(content.Contains(token, StringComparison.Ordinal), "the bare token must be fully redacted");
    }

    [Test]
    [Arguments("The class MyVeryLongConfigurationClassNameHere handles startup.", "an ordinary long identifier is low-entropy")]
    [Arguments("The user forgot their password for the third time.", "the bare word 'password' without an assignment is prose")]
    [Arguments("Used sk-learn for the model.", "sk-learn is a package name, not a token prefix")]
    public void Scan_WhenContentOnlyLooksLikeASecret_LeavesItAlone(string content, string because)
    {
        var result = ScanContent(content);

        AssertEx.False(result.ShouldReject, because);
        AssertEx.True(result.RedactedContent is null, $"nothing must be redacted: {because}");
    }

    private static MemoryProposalSecretScanner.ScanResult ScanContent(string content)
    {
        return MemoryProposalSecretScanner.Scan(type: "node_memory_proposal",
            operation: "add",
            content: content,
            evidence: [],
            confidence: "low");
    }
}
