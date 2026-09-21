namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The whole project detail in one place: the judge policy and the task items, so a create, an edit, a judge
///     change and a plain GET can never disagree about what a project detail carries.
/// </summary>
internal static class BenchmarkProjectDetailProjection
{
    public static async Task<BenchmarkProjectDetailResponse> ReadAsync(BenchmarkRecordService records,
        BenchmarkProjectRecord project,
        int runCount,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(project);

        // A plain LIST, never get-or-create: materializing item 0 for a project that has none is a write, and exactly one endpoint may perform it — the items GET.
        // Every other project read stays a read, so a page refresh cannot race two item-0 rows into existence.
        return project.ToDetail(runCount,
            await BenchmarkJudgePolicyProjection.ReadAsync(records, project.Id, ct),
            await records.ListTaskItemsAsync(project.Id, ct));
    }
}
