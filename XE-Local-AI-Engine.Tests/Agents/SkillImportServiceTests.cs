namespace XE_Local_AI_Engine.Tests.Agents;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the two-phase contract itself: the preview writes nothing, the commit refuses without an explicit
///     acknowledgement, imported skills land disabled and marked imported, conflicts default to skipping, and the
///     GitHub source never leaves its host allowlist. No test here touches the network.
/// </summary>
public sealed class SkillImportServiceTests
{
    [Test]
    public async Task Preview_ParsesFrontmatterAndWritesNothing()
    {
        using var harness = new SkillImportHarness();
        var archive = SkillImportFixtures.Zip(zip =>
        {
            zip.AddText("pdf-tools/SKILL.md",
                "---\nname: pdf-tools\ndescription: Extract text from PDFs.\nlicense: MIT\ncompatibility: any\nallowed-tools:\n  - Read\n  - Write\nmetadata:\n  author: someone\n---\n\n# PDF tools\n\nBody line.\n");
            zip.AddText("pdf-tools/references/FAQ.md", "Frequently asked.");
        });

        var preview = await harness.Service.PreviewArchiveAsync(archive).ConfigureAwait(false);
        var skill = preview.Skills.Single();

        AssertEx.Equal("pdf-tools", skill.Name);
        AssertEx.Equal("Extract text from PDFs.", skill.Description);
        AssertEx.Equal("MIT", skill.License);
        AssertEx.Equal("any", skill.Compatibility);

        // A sequence and a space-delimited scalar are the same thing to the specification; normalising once here is
        // what keeps every downstream consumer from having to know the frontmatter had a choice.
        AssertEx.Equal("Read Write", skill.AllowedTools);
        AssertEx.Equal("someone", AssertEx.NotNull(skill.Metadata)["author"]);
        AssertEx.Equal("references/FAQ.md", skill.Resources.Single().Name);
        AssertEx.True(skill.CanImport);
        AssertEx.Equal("upload", preview.SourceUri);

        // Phase 1 is a dry run in the strongest sense: not one persistence call was made.
        await harness.Store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await harness.Store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await harness.Store.DidNotReceive().ReplaceResourcesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkillResourceInput>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Preview_PrefersTheDirectoryNameAndWarnsOnMismatch()
    {
        using var harness = new SkillImportHarness();
        var archive = SkillImportFixtures.Zip(zip =>
            zip.AddText("pdf-tools/SKILL.md", SkillImportFixtures.SkillMarkdown("something-else")));

        var preview = await harness.Service.PreviewArchiveAsync(archive).ConfigureAwait(false);

        // Spec rule: the name must match the containing directory. Where they disagree the directory wins, because
        // that is the name the ecosystem addresses the skill by — but the operator is told it happened.
        AssertEx.Equal("pdf-tools", preview.Skills.Single().Name);
        AssertEx.NotEmpty(preview.Warnings);
    }

    [Test]
    public async Task Preview_MarksAnUnusableSkillAsNotImportable()
    {
        using var harness = new SkillImportHarness();
        var archive = SkillImportFixtures.Zip(zip =>
            zip.AddText("foo--bar/SKILL.md", SkillImportFixtures.SkillMarkdown("foo--bar")));

        var preview = await harness.Service.PreviewArchiveAsync(archive).ConfigureAwait(false);
        var skill = preview.Skills.Single();

        // A name MAF rejects blocks the skill in the report instead of throwing at agent-construction time later.
        AssertEx.False(skill.CanImport);
        AssertEx.NotEmpty(skill.Problems);
    }

    [Test]
    public async Task PreviewMarkdown_ImportsAPastedSkillInstructionsOnly()
    {
        using var harness = new SkillImportHarness();

        var preview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pasted-skill")).ConfigureAwait(false);

        AssertEx.Equal("pasted-skill", preview.Skills.Single().Name);
        AssertEx.Empty(preview.Skills.Single().Resources);
        AssertEx.Equal("upload", preview.SourceUri);
    }

    [Test]
    public async Task PreviewMarkdown_RejectsADocumentWithoutFrontmatter()
    {
        using var harness = new SkillImportHarness();

        var preview = await harness.Service.PreviewMarkdownAsync("# Just a heading\n\nNo frontmatter here.").ConfigureAwait(false);

        AssertEx.False(preview.Skills.Single().CanImport);
    }

    [Test]
    public async Task Commit_WithoutAcknowledgementWritesNothing()
    {
        using var harness = new SkillImportHarness();
        var preview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pdf-tools")).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"]))).ConfigureAwait(false);

        await harness.Store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Commit_LandsTheSkillDisabledWithImportedProvenance()
    {
        using var harness = new SkillImportHarness();
        var archive = SkillImportFixtures.Zip(zip =>
        {
            zip.AddText("pdf-tools/SKILL.md", SkillImportFixtures.SkillMarkdown("pdf-tools"));
            zip.AddText("pdf-tools/references/FAQ.md", "Frequently asked.");
        });
        var preview = await harness.Service.PreviewArchiveAsync(archive).ConfigureAwait(false);

        var result = await harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"], Acknowledged: true)).ConfigureAwait(false);

        AssertEx.Equal(SkillImportStatus.Imported, result.Outcomes.Single().Status);

        // Landing disabled is the strongest control in the design — the definition resolver only resolves enabled
        // skills, so third-party instructions cannot reach a model until an operator deliberately turns them on.
        var written = harness.WrittenInput(nameof(IAgentSkillStore.CreateAsync));
        AssertEx.False(written.Enabled, "An imported skill must never land enabled.");
        AssertEx.Equal(AgentSkillOrigin.Imported, written.Origin);
        AssertEx.Equal("upload", written.SourceUri);
        AssertEx.NotNullOrEmpty(written.ContentSha256);
        AssertEx.True(written.ImportedAtUtc > 0, "Provenance must record when the import happened.");

        await harness.Store.Received(1).ReplaceResourcesAsync(Arg.Any<Guid>(),
            Arg.Is<IReadOnlyList<AgentSkillResourceInput>>(resources => resources.Count == 1 && resources[0].Name == "references/FAQ.md"),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Commit_GitHubSourceRecordsTheRepositoryProvenance()
    {
        var handler = new QueuedHttpMessageHandler()
                      .EnqueueRedirect("https://codeload.github.com/acme/skills/zip/HEAD")
                      .EnqueueArchive(SkillImportFixtures.Zip(zip => zip.AddText("pdf-tools/SKILL.md", SkillImportFixtures.SkillMarkdown("pdf-tools"))));
        using var harness = new SkillImportHarness(handler);

        var preview = await harness.Service.PreviewGitHubRepositoryAsync("acme", "skills").ConfigureAwait(false);
        await harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"], Acknowledged: true)).ConfigureAwait(false);

        // github.com → codeload.github.com is a normal hop and must be followed, one host revalidation per hop.
        AssertEx.Equal(expected: 2, handler.RequestedUris.Count);
        AssertEx.Equal("github.com", handler.RequestedUris[0].Host);
        AssertEx.Equal("codeload.github.com", handler.RequestedUris[1].Host);
        AssertEx.Equal("github:acme/skills", preview.SourceUri);

        AssertEx.Equal("github:acme/skills", harness.WrittenInput(nameof(IAgentSkillStore.CreateAsync)).SourceUri);
    }

    [Test]
    public async Task PreviewGitHub_RefusesARedirectOffTheHostAllowlist()
    {
        var handler = new QueuedHttpMessageHandler().EnqueueRedirect("https://evil.example.com/acme/skills/zip/HEAD");
        using var harness = new SkillImportHarness(handler);

        var exception = await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.PreviewGitHubRepositoryAsync("acme", "skills")).ConfigureAwait(false);

        AssertEx.Contains(exception.Message, "allowlist");
        AssertEx.Equal(expected: 1, handler.RequestedUris.Count, "The off-allowlist hop must never be requested.");
    }

    [Test]
    [Arguments("", "skills")]
    [Arguments("acme", "")]
    [Arguments(".hidden", "skills")]
    [Arguments("acme", ".git")]
    [Arguments("acme/evil", "skills")]
    [Arguments("acme", "../../etc")]
    [Arguments("ac me", "skills")]
    [Arguments("https://evil.example.com", "skills")]
    public async Task PreviewGitHub_RejectsAMalformedSlugWithoutMakingARequest(string owner, string repository)
    {
        using var harness = new SkillImportHarness();

        await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.PreviewGitHubRepositoryAsync(owner, repository)).ConfigureAwait(false);

        AssertEx.Empty(harness.Handler.RequestedUris, "A malformed slug must be refused before any request is made.");
    }

    [Test]
    public async Task Commit_DefaultsToSkippingAConflictAndCanBeToldToReplace()
    {
        using var harness = new SkillImportHarness();
        harness.SeedExistingSkills("pdf-tools");
        var existingId = (await harness.Store.ListAsync().ConfigureAwait(false)).Single().Id;

        var preview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pdf-tools")).ConfigureAwait(false);
        AssertEx.True(preview.Skills.Single().ConflictsWithExistingSkill);

        var skipped = await harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"], Acknowledged: true)).ConfigureAwait(false);
        AssertEx.Equal(SkillImportStatus.Skipped, skipped.Outcomes.Single().Status);
        await harness.Store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);

        var replacePreview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pdf-tools")).ConfigureAwait(false);
        var replaced = await harness.Service.CommitAsync(new SkillImportCommitRequest(replacePreview.Token,
            ["pdf-tools"],
            SkillImportConflictResolution.Replace,
            Acknowledged: true)).ConfigureAwait(false);

        AssertEx.Equal(SkillImportStatus.Replaced, replaced.Outcomes.Single().Status);
        await harness.Store.Received(1).UpdateAsync(existingId, Arg.Is<AgentSkillInput>(input => !input.Enabled && input.Origin == AgentSkillOrigin.Imported), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task Commit_ConsumesTheTokenSoTheSamePreviewCannotBeReplayed()
    {
        using var harness = new SkillImportHarness();
        var preview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pdf-tools")).ConfigureAwait(false);

        await harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"], Acknowledged: true)).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools"], Acknowledged: true))).ConfigureAwait(false);

        await harness.Store.Received(1).CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Commit_RejectsASelectionThatIsNotInTheApprovedPreview()
    {
        using var harness = new SkillImportHarness();
        var preview = await harness.Service.PreviewMarkdownAsync(SkillImportFixtures.SkillMarkdown("pdf-tools")).ConfigureAwait(false);

        // Phase 2 replays the materialised payload only. A name the operator never saw cannot be smuggled in, and the
        // whole commit is refused before anything is written rather than partially applied.
        await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.CommitAsync(new SkillImportCommitRequest(preview.Token, ["pdf-tools", "other-skill"], Acknowledged: true))).ConfigureAwait(false);

        await harness.Store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Commit_RejectsAnUnknownToken()
    {
        using var harness = new SkillImportHarness();

        await AssertEx.ThrowsAsync<SkillImportException>(() =>
            harness.Service.CommitAsync(new SkillImportCommitRequest(Guid.NewGuid(), ["pdf-tools"], Acknowledged: true))).ConfigureAwait(false);

        await harness.Store.DidNotReceive().CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }
}
