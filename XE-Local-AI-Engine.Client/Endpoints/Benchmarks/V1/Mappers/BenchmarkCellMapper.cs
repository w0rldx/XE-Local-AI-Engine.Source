namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class BenchmarkCellMapper
{
    public static ListBenchmarkCellsResponse ToResponse(this BenchmarkCellPage page) =>
        new()
        {
            Cells =
            [
                .. page.Cells.Select(static cell => new BenchmarkCellResponse
                {
                    CellKey = cell.CellKey,
                    PrimaryModelName = cell.PrimaryModelName,
                    ModelContentFingerprint = cell.ModelContentFingerprint,
                    KvCacheType = cell.KvCacheType,
                    AgentDefinitionId = cell.AgentDefinitionId,
                    AgentName = cell.AgentName,
                    AgentVersion = cell.AgentVersion,
                    ReasoningBudgetTokens = cell.ReasoningBudgetTokens,
                    RepeatGroupId = cell.RepeatGroupId,
                    RepeatIndex = cell.RepeatIndex,
                    Quality = cell.Quality,
                    Rank = cell.Rank,
                    RankExclusionReason = cell.RankExclusionReason,
                    Items =
                    [
                        .. cell.Items.Select(static item => new BenchmarkCellItemResponse
                        {
                            RunId = item.RunId,
                            TaskItemId = item.TaskItemId,
                            TaskItemIndex = item.TaskItemIndex,
                            QualityScore = item.QualityScore,
                            PrimaryStopReason = item.PrimaryStopReason,
                            RankExclusionReason = item.RankExclusionReason,
                            ReasoningTokens = item.ReasoningTokens,
                            DurationMs = item.DurationMs,
                            ReasoningAtBudget = BenchmarkReasoningReadout.ReasoningAtBudget(item.ReasoningTokens, item.AppliedReasoningBudgetTokens, item.ToolCallCount, item.SegmentCount),
                            NonReasoningOutputTokens = BenchmarkReasoningReadout.NonReasoningOutputTokens(item.GenerationTokens, item.ReasoningTokens, item.ToolCallCount, item.SegmentCount)
                        })
                    ]
                })
            ],
            RankCohort = new BenchmarkRankCohortResponse
            {
                PolicyRevision = page.RankCohort?.PolicyRevision,
                ExecutionKey = page.RankCohort?.ExecutionKey,
                CohortGeneration = page.RankCohort?.CohortGeneration,
                RankedCount = page.RankCohort?.RankedCount ?? 0,
                TotalScored = page.RankCohort?.TotalScored ?? 0
            },
            ScorableItemCount = page.ScorableItemCount
        };
}
