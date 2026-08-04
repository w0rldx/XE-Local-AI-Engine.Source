namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

internal static class SkillMapper
{
    public static SkillResponse ToResponse(this AgentSkillRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SkillResponse
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Body = record.Body,
            Enabled = record.Enabled,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            License = record.License,
            Compatibility = record.Compatibility,
            AllowedTools = record.AllowedTools,
            Metadata = record.Metadata,
            Origin = record.Origin,
            SourceUri = record.SourceUri,
            ImportedAtUtc = record.ImportedAtUtc,
            ResourceCount = record.Resources?.Count ?? 0
        };
    }

    public static SkillSummaryResponse ToSummary(this AgentSkillRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SkillSummaryResponse
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Enabled = record.Enabled,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            License = record.License,
            Compatibility = record.Compatibility,
            AllowedTools = record.AllowedTools,
            Metadata = record.Metadata,
            Origin = record.Origin,
            SourceUri = record.SourceUri,
            ImportedAtUtc = record.ImportedAtUtc
        };
    }

    public static SkillResourceSummaryResponse ToSummary(this AgentSkillResourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SkillResourceSummaryResponse
        {
            Name = record.Name,
            Description = record.Description,
            MediaType = record.MediaType,
            SizeBytes = record.SizeBytes
        };
    }

    public static SkillResourceResponse ToResponse(this AgentSkillResourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new SkillResourceResponse
        {
            Name = record.Name,
            Description = record.Description,
            MediaType = record.MediaType,
            SizeBytes = record.SizeBytes,
            Content = record.Content
        };
    }

    /// <summary>
    ///     Projects the dry-run report onto the wire. Resource <em>content</em> is dropped here: the preview shows the
    ///     operator which bundled files a skill carries, and shipping every payload would put megabytes of third-party
    ///     text in a report that exists to be read.
    /// </summary>
    public static SkillImportPreviewResponse ToResponse(this SkillImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        return new SkillImportPreviewResponse
        {
            Token = preview.Token,
            SourceUri = preview.SourceUri,
            Warnings = preview.Warnings,
            Skills = [.. preview.Skills.Select(static skill => new SkillImportCandidateResponse
            {
                Name = skill.Name,
                Description = skill.Description,
                Body = skill.Body,
                License = skill.License,
                Compatibility = skill.Compatibility,
                AllowedTools = skill.AllowedTools,
                Metadata = skill.Metadata,
                BodySizeBytes = skill.BodySizeBytes,
                BodyLineCount = skill.BodyLineCount,
                Resources = [.. skill.Resources.Select(static resource => new SkillResourceSummaryResponse
                {
                    Name = resource.Name,
                    Description = resource.Description,
                    MediaType = resource.MediaType,
                    SizeBytes = resource.SizeBytes
                })],
                RefusedScripts = skill.RefusedScripts,
                ConflictsWithExistingSkill = skill.ConflictsWithExistingSkill,
                Problems = skill.Problems,
                CanImport = skill.CanImport
            })]
        };
    }

    public static SkillImportCommitResponse ToResponse(this SkillImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SkillImportCommitResponse
        {
            Outcomes = [.. result.Outcomes.Select(static outcome => new SkillImportOutcomeResponse
            {
                Name = outcome.Name,
                Status = outcome.Status,
                Reason = outcome.Reason
            })]
        };
    }

    public static AgentSkillInput ToInput(this CreateSkillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentSkillInput(request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.Body ?? string.Empty,
            Enabled: true,
            request.License,
            request.Compatibility,
            request.AllowedTools,
            request.Metadata);
    }

    public static AgentSkillInput ToInput(this UpdateSkillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Origin/SourceUri/ImportedAtUtc are deliberately absent: the store treats provenance as promote-only and
        // leaves absent values alone, so an operator edit can never launder an imported skill into a local one.
        return new AgentSkillInput(request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.Body ?? string.Empty,
            request.Enabled,
            request.License,
            request.Compatibility,
            request.AllowedTools,
            request.Metadata);
    }
}
