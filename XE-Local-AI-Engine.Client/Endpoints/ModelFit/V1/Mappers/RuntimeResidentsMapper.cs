namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.ModelFit;

internal static class RuntimeResidentsMapper
{
    public static RuntimeResidentsResponse ToResponse(this IReadOnlyList<RuntimeResident> residents)
    {
        ArgumentNullException.ThrowIfNull(residents);

        return new RuntimeResidentsResponse
        {
            Items =
            [
                .. residents.Select(static resident => new RuntimeResidentResponse
                {
                    Runtime = resident.Runtime.ToDto(),
                    ModelId = resident.ModelId,
                    State = resident.State.ToDto(),
                    Backend = resident.Backend?.ToDto(),
                    CanEject = resident.CanEject
                })
            ]
        };
    }

    private static RuntimeResidentKindDto ToDto(this RuntimeResidentKind runtime)
    {
        return runtime switch
        {
            RuntimeResidentKind.Image => RuntimeResidentKindDto.Image,
            RuntimeResidentKind.Transcription => RuntimeResidentKindDto.Transcription,
            _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "Unknown resident runtime.")
        };
    }

    private static RuntimeResidentStateDto ToDto(this RuntimeResidentState state)
    {
        return state switch
        {
            RuntimeResidentState.Starting => RuntimeResidentStateDto.Starting,
            RuntimeResidentState.Idle => RuntimeResidentStateDto.Idle,
            RuntimeResidentState.Active => RuntimeResidentStateDto.Active,
            RuntimeResidentState.Exited => RuntimeResidentStateDto.Exited,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown resident state.")
        };
    }
}
