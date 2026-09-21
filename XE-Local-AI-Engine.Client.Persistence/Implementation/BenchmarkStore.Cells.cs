namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkCellPage> ListCellsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // ONE ranking, then one flat read of the runs it ranked — the same two reads the export makes, grouped by the key the ranking already decided rather than by
        // re-deriving anything. Warm-ups never form a rankable cell, so they are absent here for the same reason they are absent from the denominator.
        var ranking = await LoadRankingAsync(projectId, cancellationToken);
        var rows = await _dbContext.BenchmarkRuns.AsNoTracking()
                                   .Where(entity => entity.ProjectId == projectId && !entity.IsWarmup)
                                   .OrderBy(entity => entity.CreatedAtUtc)
                                   .Select(entity => new
                                   {
                                       entity.Id,
                                       entity.CellKey,
                                       entity.PrimaryModelName,
                                       entity.ModelContentFingerprint,
                                       entity.PrimaryKvCacheType,
                                       entity.RepeatGroupId,
                                       entity.RepeatIndex,
                                       entity.TaskItemId,
                                       entity.TaskItemIndex,
                                       entity.PrimaryStopReason
                                   })
                                   .ToArrayAsync(cancellationToken);

        var cells = new List<BenchmarkCellRecord>();
        foreach (var group in rows.GroupBy(static row => row.CellKey, StringComparer.Ordinal))
        {
            // A run inserted between the ranking read and this one is not in either map; it is skipped rather than
            // throwing, because a freeze landing mid-read is ordinary and the next read will carry it.
            var members = group.Where(row => ranking.Runs.ContainsKey(row.Id))
                               .OrderBy(static row => row.TaskItemIndex ?? int.MinValue)
                               .ThenBy(static row => row.Id)
                               .ToArray();
            if (members.Length == 0)
            {
                continue;
            }

            var cell = ranking.Cells.TryGetValue(group.Key, out var entry) ? entry : new CellRanking { Quality = null, Reason = null, Countable = false };
            cells.Add(new BenchmarkCellRecord
            {
                CellKey = group.Key,
                PrimaryModelName = members[0].PrimaryModelName,
                ModelContentFingerprint = members[0].ModelContentFingerprint,
                KvCacheType = members[0].PrimaryKvCacheType,
                RepeatGroupId = members[0].RepeatGroupId,
                RepeatIndex = members[0].RepeatIndex,
                Quality = cell.Quality,
                // Every run of a cell reports its cell's rank, so the first one carries it.
                Rank = ranking.Runs[members[0].Id].Rank,
                RankExclusionReason = cell.Reason,
                Items = [
                    .. members.Select(member => new BenchmarkCellItemRecord
                    {
                        RunId = member.Id,
                        TaskItemId = member.TaskItemId,
                        TaskItemIndex = member.TaskItemIndex,
                        QualityScore = ranking.Runs[member.Id].QualityScore,
                        PrimaryStopReason = member.PrimaryStopReason,
                        RankExclusionReason = ranking.Runs[member.Id].Judge.RankExclusionReason
                    })
                ]
            });
        }

        return new BenchmarkCellPage { Cells = cells, RankCohort = ranking.Cohort, ScorableItemCount = ranking.ScorableItemCount };
    }
}
