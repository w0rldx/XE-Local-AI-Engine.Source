namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What a managed source build is allowed to compile. A build runs repository code with the app user's
///     privileges, so the server pins the official source itself and every other shape has to be spelled out by an
///     operator who acknowledged that.
/// </summary>
public sealed class WhisperCppSourceBuildRequestValidationTests
{
    [Test]
    public void Normalize_OfficialSource_PinsTheEngineRepository()
    {
        // The caller does not get to name the official repository: the server fills it in, so a request that lies
        // about what "official" means cannot be smuggled through.
        var normalized = WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cuda, WhisperCppSourceSelection.Official));

        AssertEx.Equal(WhisperCppSourceBuildRequestValidation.OfficialRepository, normalized.Repository);
        AssertEx.Null(normalized.Commit, "The official source builds the engine-pinned revision, never a caller's commit.");

        // Idempotent by construction: the installed-runtime store validates a persisted repository by round-tripping
        // it through this normalizer, so a second pass that changed its answer would tombstone every healthy record.
        var again = WhisperCppSourceBuildRequestValidation.Normalize(normalized);
        AssertEx.Equal(normalized, again);
    }

    [Test]
    public void Normalize_OfficialSourceWithACommit_Throws()
    {
        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cuda,
                WhisperCppSourceSelection.Official,
                Commit: new string(c: 'a', count: 40))));

        AssertEx.Contains(exception.Message, "engine-pinned revision");
    }

    [Test]
    public void Normalize_CustomSourceWithoutAcknowledgement_Throws()
    {
        // The acknowledgement is the whole point: a custom repository's build scripts execute locally.
        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cuda,
                WhisperCppSourceSelection.Custom,
                "https://github.com/someone/whisper.cpp",
                AcknowledgeCustomSourceRisk: false)));

        AssertEx.Contains(exception.Message, "acknowledgement");
    }

    [Test]
    [Arguments("http://github.com/owner/repo", "plain http")]
    [Arguments("https://gitlab.com/owner/repo", "a host that is not github.com")]
    [Arguments("https://github.com/owner", "no repository segment")]
    [Arguments("https://github.com/owner/repo/extra", "an extra path segment")]
    [Arguments("https://user@github.com/owner/repo", "embedded credentials")]
    [Arguments("https://github.com/owner/repo?ref=main", "a query string")]
    [Arguments("https://github.com:8443/owner/repo", "a non-default port")]
    public void Normalize_NonGitHubRepository_Throws(string repository, string because)
    {
        _ = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cuda,
                WhisperCppSourceSelection.Custom,
                repository,
                AcknowledgeCustomSourceRisk: true)));

        AssertEx.NotEmpty(because);
    }

    [Test]
    [Arguments("abc123")]
    [Arguments("0123456789012345678901234567890123456789x")]
    [Arguments("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Normalize_ShortCommit_Throws(string commit)
    {
        // Only a full 40-character SHA identifies a revision unambiguously; an abbreviation can become ambiguous as
        // the repository grows.
        var exception = AssertEx.Throws<WhisperRuntimeException>(() =>
            WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cuda,
                WhisperCppSourceSelection.Custom,
                "https://github.com/owner/repo",
                commit,
                AcknowledgeCustomSourceRisk: true)));

        AssertEx.Contains(exception.Message, "40-character");
    }

    [Test]
    public void Normalize_CustomSource_CanonicalizesTheRepositoryAndTheCommit()
    {
        var normalized = WhisperCppSourceBuildRequestValidation.Normalize(new WhisperCppSourceBuildRequest(WhisperBackend.Cpu,
            WhisperCppSourceSelection.Custom,
            "https://github.com/Owner/Repo.git",
            new string(c: 'A', count: 40),
            AcknowledgeCustomSourceRisk: true));

        AssertEx.Equal("https://github.com/Owner/Repo", normalized.Repository);
        AssertEx.Equal(new string(c: 'a', count: 40), normalized.Commit);
        AssertEx.Equal(normalized, WhisperCppSourceBuildRequestValidation.Normalize(normalized));
    }
}
