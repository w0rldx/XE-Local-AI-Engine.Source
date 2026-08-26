namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkCellPage> ListCellsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // ONE ranking, then one flat read of the runs it ranked — the same two reads the export makes, grouped by the
        // key the ranking already decided rather than by re-deriving anything. Warm-ups never form a rankable cell, so
        // they are absent here for the same reason they are absent from the denominator.
        var ranking = await LoadRankingAsync(projectId, cancellationToken).ConfigureAwait(false);
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
                                   .ToArrayAsync(cancellationToken)
                                   .ConfigureAwait(false);

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

            var cell = ranking.Cells.TryGetValue(group.Key, out var entry) ? entry : new CellRanking(null, null, Countable: false);
            cells.Add(new BenchmarkCellRecord(group.Key,
                members[0].PrimaryModelName,
                members[0].ModelContentFingerprint,
                members[0].PrimaryKvCacheType,
                members[0].RepeatGroupId,
                members[0].RepeatIndex,
                cell.Quality,

                // Every run of a cell reports its cell's rank, so the first one carries it.
                ranking.Runs[members[0].Id].Rank,
                cell.Reason,
                [
                    .. members.Select(member => new BenchmarkCellItemRecord(member.Id,
                        member.TaskItemId,
                        member.TaskItemIndex,
                        ranking.Runs[member.Id].QualityScore,
                        member.PrimaryStopReason,
                        ranking.Runs[member.Id].Judge.RankExclusionReason))
                ]));
        }

        return new BenchmarkCellPage(cells, ranking.Cohort, ranking.ScorableItemCount);
    }

}
