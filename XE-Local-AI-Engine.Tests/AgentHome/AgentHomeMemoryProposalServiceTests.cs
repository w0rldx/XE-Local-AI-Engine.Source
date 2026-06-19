namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Memory-proposal export coverage: schema validation, secret scanning, and collector behaviour.
///     No Docker, no real sandbox — the service reads host-side JSONL files written by the agent (or test fixtures).
/// </summary>
public sealed class AgentHomeMemoryProposalServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }


    [Test]
    public async Task CollectAsync_WhenNoProposalsDirectory_ReturnsEmpty()
    {
        var root = NewTempRunDir("run-no-dir");
        // No memory/proposals subdir created.

        var result = await CreateService().CollectAsync(Request("run-no-dir", root));

        AssertEx.Empty(result.Proposals, "no proposals when the directory does not exist");
        AssertEx.Empty(result.Rejections, "no rejections when the directory does not exist");
    }


    [Test]
    public async Task CollectAsync_ValidNodeMemoryProposal_Accepted()
    {
        var root = NewTempRunDir("run-valid-node");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"The project uses TUnit.","evidence":["/agent-home/workspace/selected/repo-01/readme.md"],"confidence":"high"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-valid-node", root));

        AssertEx.Equal(1, result.Proposals.Count, "one valid proposal accepted");
        AssertEx.Empty(result.Rejections);
        var p = result.Proposals[0];
        AssertEx.Equal("node_memory_proposal", p.Type);
        AssertEx.Equal("add", p.Operation);
        AssertEx.Equal("high", p.Confidence);
        AssertEx.Equal("node-memory.proposals.jsonl", p.SourceFileName);
        AssertEx.Equal(0, p.SourceLineIndex);
    }

    [Test]
    public async Task CollectAsync_ValidProjectMemoryProposal_Accepted()
    {
        var root = NewTempRunDir("run-valid-project");
        WriteProposalFile(root, "project-memory.proposals.jsonl",
        [
            """{"type":"project_memory_proposal","operation":"update","content":"Build script updated.","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-valid-project", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Equal("project_memory_proposal", result.Proposals[0].Type);
        AssertEx.Equal("update", result.Proposals[0].Operation);
        AssertEx.Equal("medium", result.Proposals[0].Confidence);
    }

    [Test]
    public async Task CollectAsync_MultipleFiles_CombinesProposals()
    {
        var root = NewTempRunDir("run-multi");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Note A.","evidence":[],"confidence":"low"}"""
        ]);
        WriteProposalFile(root, "project-memory.proposals.jsonl",
        [
            """{"type":"project_memory_proposal","operation":"remove","content":"Note B.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-multi", root));

        AssertEx.Equal(2, result.Proposals.Count, "proposals from both files are combined");
        AssertEx.Empty(result.Rejections);
    }

    [Test]
    public async Task CollectAsync_BlankLinesInJsonl_SkippedSilently()
    {
        var root = NewTempRunDir("run-blanks");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            "",
            """{"type":"node_memory_proposal","operation":"add","content":"Valid.","evidence":[],"confidence":"low"}""",
            "   "
        ]);

        var result = await CreateService().CollectAsync(Request("run-blanks", root));

        AssertEx.Equal(1, result.Proposals.Count, "only the one valid non-blank line is accepted");
        AssertEx.Empty(result.Rejections);
    }


    [Test]
    public async Task CollectAsync_InvalidJson_Rejected()
    {
        var root = NewTempRunDir("run-invalid-json");
        WriteProposalFile(root, "node-memory.proposals.jsonl", ["not json at all"]);

        var result = await CreateService().CollectAsync(Request("run-invalid-json", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "not valid JSON");
    }

    [Test]
    public async Task CollectAsync_UnknownType_Rejected()
    {
        var root = NewTempRunDir("run-bad-type");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"bad_type","operation":"add","content":"X","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-bad-type", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "type");
    }

    [Test]
    public async Task CollectAsync_UnknownOperation_Rejected()
    {
        var root = NewTempRunDir("run-bad-op");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"upsert","content":"X","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-bad-op", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "operation");
    }

    [Test]
    public async Task CollectAsync_UnknownConfidence_Rejected()
    {
        var root = NewTempRunDir("run-bad-conf");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"X","evidence":[],"confidence":"certain"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-bad-conf", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "confidence");
    }

    [Test]
    public async Task CollectAsync_ContentTooLong_Rejected()
    {
        var root = NewTempRunDir("run-long-content");
        var longContent = new string('x', 4001);
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            $$"""{"type":"node_memory_proposal","operation":"add","content":"{{longContent}}","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-long-content", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "content");
    }

    [Test]
    public async Task CollectAsync_EmptyContent_Rejected()
    {
        var root = NewTempRunDir("run-empty-content");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-empty-content", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
    }

    [Test]
    public async Task CollectAsync_MissingRequiredField_Rejected()
    {
        var root = NewTempRunDir("run-missing-field");
        // No "confidence" field.
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"X","evidence":[]}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-missing-field", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "confidence");
    }

    [Test]
    public async Task CollectAsync_EvidencePathTraversal_Rejected()
    {
        var root = NewTempRunDir("run-traversal");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"X","evidence":["/agent-home/../etc/passwd"],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-traversal", root));

        AssertEx.Empty(result.Proposals);
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "path-traversal");
    }

    [Test]
    public async Task CollectAsync_MixedValidAndInvalid_CorrectCounts()
    {
        var root = NewTempRunDir("run-mixed");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Good record.","evidence":[],"confidence":"high"}""",
            "bad json",
            """{"type":"node_memory_proposal","operation":"add","content":"Another good record.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-mixed", root));

        AssertEx.Equal(2, result.Proposals.Count, "two valid records accepted");
        AssertEx.Equal(1, result.Rejections.Count, "one bad-json record rejected");
    }


    [Test]
    public async Task CollectAsync_SourceLineIndexTracked()
    {
        var root = NewTempRunDir("run-line-idx");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Line 0.","evidence":[],"confidence":"low"}""",
            "bad",
            """{"type":"node_memory_proposal","operation":"add","content":"Line 2.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-line-idx", root));

        AssertEx.Equal(2, result.Proposals.Count);
        AssertEx.Equal(0, result.Proposals[0].SourceLineIndex);
        AssertEx.Equal(2, result.Proposals[1].SourceLineIndex);
        AssertEx.Equal(1, result.Rejections[0].SourceLineIndex);
    }


    [Test]
    public async Task CollectAsync_PemPrivateKeyInContent_RecordRejected()
    {
        var root = NewTempRunDir("run-pem");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"key: -----BEGIN RSA PRIVATE KEY-----\nMIIE...","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-pem", root));

        AssertEx.Empty(result.Proposals, "PEM private-key record must be rejected");
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "private-key");
    }

    [Test]
    public async Task CollectAsync_GoogleServiceAccountJsonInContent_RecordRejected()
    {
        var root = NewTempRunDir("run-sa");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"{\"type\": \"service_account\", \"private_key\": \"secret\"}","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-sa", root));

        AssertEx.Empty(result.Proposals, "Google service-account JSON must be rejected");
        AssertEx.Equal(1, result.Rejections.Count);
    }

    [Test]
    public async Task CollectAsync_SecretInEvidencePath_RecordRejected()
    {
        var root = NewTempRunDir("run-secret-evidence");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Normal content.","evidence":["/agent-home/workspace/selected/repo-01/config?api_key=AKIA1234567890ABCDEF"],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-secret-evidence", root));

        AssertEx.Empty(result.Proposals, "secret in evidence path must reject the whole record");
        AssertEx.Equal(1, result.Rejections.Count);
    }


    [Test]
    public async Task CollectAsync_GitHubTokenInContent_RedactedNotRejected()
    {
        var root = NewTempRunDir("run-gh-token");
        // ghp_ + 36 alphanumeric chars = valid GitHub token shape.
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Token: ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890AB","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-gh-token", root));

        AssertEx.Equal(1, result.Proposals.Count, "record with redactable token survives");
        AssertEx.Empty(result.Rejections);
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:github-token]", StringComparison.Ordinal,
            "GitHub token replaced by redaction placeholder");
    }

    [Test]
    public async Task CollectAsync_AwsAccessKeyInContent_RedactedNotRejected()
    {
        var root = NewTempRunDir("run-aws");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Access key: AKIAIOSFODNN7EXAMPLE123","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-aws", root));

        AssertEx.Equal(1, result.Proposals.Count, "AWS key redacted; record survives");
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:aws-access-key]");
    }

    [Test]
    public async Task CollectAsync_JwtInContent_RedactedNotRejected()
    {
        var root = NewTempRunDir("run-jwt");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c was used.","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-jwt", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:jwt]");
    }

    [Test]
    public async Task CollectAsync_SlackTokenInContent_RedactedNotRejected()
    {
        var root = NewTempRunDir("run-slack");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Slack token: xoxb-123456789012-abcdefghijklmnop","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-slack", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:slack-token]");
    }


    [Test]
    public async Task CollectAsync_HighEntropyBearerAtNonZeroOffset_RedactsTokenKeepsKeywordNoLeakNoThrow()
    {
        // Regression for MemoryProposalSecretScanner.RedactHighEntropyBearer: the keyword-length slice used the
        // ABSOLUTE capture-group index against the matched substring. At a non-zero content offset that either leaked
        // token bytes into the "redacted" output or threw ArgumentOutOfRangeException (which would escape the
        // never-throws-on-a-bad-record contract). Here the match starts well past offset 0.
        const string token = "aB3xK9mP2qR7sT4vW8yZ1cD5fG6hJ0kL"; // 32 chars, Shannon entropy 5.0 (>= 4.5).
        var root = NewTempRunDir("run-bearer-offset");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            $$"""{"type":"node_memory_proposal","operation":"add","content":"The deploy script set the header to Bearer {{token}} before calling the API.","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-bearer-offset", root));

        AssertEx.Empty(result.Rejections, "a redactable token must not throw or reject — the record survives redacted");
        AssertEx.Equal(1, result.Proposals.Count);
        var content = result.Proposals[0].Content;
        AssertEx.Contains(content, "[REDACTED:high-entropy-token]");
        AssertEx.Contains(content, "Bearer ", StringComparison.Ordinal, "the keyword prefix must be preserved");
        AssertEx.False(content.Contains(token, StringComparison.Ordinal), "ZERO token bytes may survive");
        // No leading fragment of the token may survive either (the buggy slice leaked the token's first bytes).
        AssertEx.False(content.Contains(token[..8], StringComparison.Ordinal), "no leading fragment of the token may survive");
    }


    [Test]
    public async Task CollectAsync_BareHighEntropyToken_RedactedNotRejected()
    {
        // No Bearer/sk-/token/key keyword — a bare high-entropy base64-ish run must still be redacted by the fallback.
        const string token = "Zk7Qw2Np5Rt8Yx1Cv4Bm9Df6Gh3Jl0Ks"; // 33 chars, entropy 5.0.
        var root = NewTempRunDir("run-bare-entropy");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            $$"""{"type":"node_memory_proposal","operation":"add","content":"The value was {{token}} in the config.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-bare-entropy", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Empty(result.Rejections);
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:high-entropy-token]");
        AssertEx.False(result.Proposals[0].Content.Contains(token, StringComparison.Ordinal), "the bare token must be fully redacted");
    }

    [Test]
    public async Task CollectAsync_OrdinaryLongIdentifier_NotRedacted()
    {
        // A long but low-entropy identifier (entropy < 4.5) must NOT be redacted by the high-entropy fallback.
        var root = NewTempRunDir("run-long-ident");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"The class MyVeryLongConfigurationClassNameHere handles startup.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-long-ident", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.False(result.Proposals[0].Content.Contains("[REDACTED", StringComparison.Ordinal),
            "an ordinary long identifier (low entropy) must not be redacted");
    }


    [Test]
    public async Task CollectAsync_AbsoluteHostPathInEvidence_RecordRejected()
    {
        var root = NewTempRunDir("run-host-evidence");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Saw something.","evidence":["/home/user/.ssh/id_rsa"],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-host-evidence", root));

        AssertEx.Empty(result.Proposals, "an absolute host path in evidence must reject the record");
        AssertEx.Equal(1, result.Rejections.Count);
        AssertEx.Contains(result.Rejections[0].Reason, "absolute host path");
    }

    [Test]
    public async Task CollectAsync_WindowsDriveHostPathInEvidence_RecordRejected()
    {
        var root = NewTempRunDir("run-win-evidence");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Saw something.","evidence":["C:\\Users\\dev\\secret.txt"],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-win-evidence", root));

        AssertEx.Empty(result.Proposals, "a Windows drive-rooted host path in evidence must reject the record");
        AssertEx.Equal(1, result.Rejections.Count);
    }

    [Test]
    public async Task CollectAsync_RelativeEvidencePath_Accepted()
    {
        // A sandbox-relative (non-rooted) evidence path is allowed alongside the workspace-rooted form.
        var root = NewTempRunDir("run-relative-evidence");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"See the file.","evidence":["repo-01/src/App.cs"],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-relative-evidence", root));

        AssertEx.Equal(1, result.Proposals.Count, "a relative evidence path is allowed");
        AssertEx.Empty(result.Rejections);
    }


    [Test]
    public async Task CollectAsync_BenignWordPassword_NotRedacted()
    {
        // The word "password" in prose without an assignment operator must not trigger redaction.
        var root = NewTempRunDir("run-benign-password");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"The user forgot their password for the third time.","evidence":[],"confidence":"low"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-benign-password", root));

        AssertEx.Equal(1, result.Proposals.Count, "benign word 'password' in prose must not be redacted");
        AssertEx.False(result.Proposals[0].Content.Contains("[REDACTED", StringComparison.Ordinal),
            "content must not contain a REDACTED placeholder");
    }

    [Test]
    public async Task CollectAsync_FakeSkLearnPrefix_NotRedacted()
    {
        // "sk-learn" is a Python package name, not a secret token prefix.
        var root = NewTempRunDir("run-sklearn");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Used sk-learn for the model.","evidence":[],"confidence":"high"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-sklearn", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.False(result.Proposals[0].Content.Contains("[REDACTED", StringComparison.Ordinal),
            "sk-learn (short, low entropy) must not be redacted");
    }

    [Test]
    public async Task CollectAsync_NormalEvidencePaths_Accepted()
    {
        var root = NewTempRunDir("run-evidence-paths");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"See the readme.","evidence":["/agent-home/workspace/selected/repo-01/README.md","/agent-home/workspace/selected/repo-01/src/App.cs"],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-evidence-paths", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Equal(2, result.Proposals[0].Evidence.Count);
    }


    [Test]
    public async Task CollectAsync_AzureConnectionStringInContent_RedactedNotRejected()
    {
        var root = NewTempRunDir("run-azure");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"add","content":"Config: DefaultEndpointsProtocol=https;AccountName=devstore;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;EndpointSuffix=core.windows.net","evidence":[],"confidence":"medium"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-azure", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Contains(result.Proposals[0].Content, "[REDACTED:azure-connection-string]");
    }


    [Test]
    public async Task CollectAsync_RemoveOperationWithContent_Accepted()
    {
        // "remove" operations still need non-empty content (the thing to remove).
        var root = NewTempRunDir("run-remove");
        WriteProposalFile(root, "node-memory.proposals.jsonl",
        [
            """{"type":"node_memory_proposal","operation":"remove","content":"Old fact no longer accurate.","evidence":[],"confidence":"high"}"""
        ]);

        var result = await CreateService().CollectAsync(Request("run-remove", root));

        AssertEx.Equal(1, result.Proposals.Count);
        AssertEx.Equal("remove", result.Proposals[0].Operation);
    }


    private static IAgentHomeMemoryProposalService CreateService()
    {
        return new AgentHomeMemoryProposalService(NullLogger<AgentHomeMemoryProposalService>.Instance);
    }

    private static MemoryProposalCollectRequest Request(string runId, string hostRunDirectory)
    {
        return new MemoryProposalCollectRequest
        {
            RunId = runId,
            HostRunDirectory = hostRunDirectory
        };
    }

    private string NewTempRunDir(string runId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "xe-tests-marker-h", runId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteProposalFile(string hostRunDirectory, string fileName, IEnumerable<string> lines)
    {
        var proposalsDir = Path.Combine(hostRunDirectory, "memory", "proposals");
        Directory.CreateDirectory(proposalsDir);
        File.WriteAllLines(Path.Combine(proposalsDir, fileName), lines);
    }
}
