namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;

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
            UpdatedAtUtc = record.UpdatedAtUtc
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
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static AgentSkillInput ToInput(this CreateSkillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentSkillInput(request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.Body ?? string.Empty);
    }

    public static AgentSkillInput ToInput(this UpdateSkillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentSkillInput(request.Name ?? string.Empty,
            request.Description ?? string.Empty,
            request.Body ?? string.Empty,
            request.Enabled);
    }
}
