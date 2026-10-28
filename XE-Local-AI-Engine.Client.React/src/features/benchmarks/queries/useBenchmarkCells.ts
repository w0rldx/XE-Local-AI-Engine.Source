import { useQuery } from "@tanstack/react-query";

import { compareBenchmarkCells, listBenchmarkCells } from "@/core/api/generated";
import { compareBenchmarkCellsQueryKey, listBenchmarkCellsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import type { BenchmarkCell, BenchmarkPairedDelta } from "@/features/benchmarks/models/BenchmarkCells";
import { toBenchmarkCell, toBenchmarkPairedDelta } from "@/features/benchmarks/models/BenchmarkCells";
import { toBenchmarkRankCohort } from "@/features/benchmarks/models/BenchmarkMappers";
import type { BenchmarkRankCohort } from "@/features/benchmarks/models/BenchmarkModels";

/** The ranked cell table: one row per (model, KV, repeat group), each holding its per-item answers. */
export interface BenchmarkCellList {
	cells: BenchmarkCell[];
	cohort: BenchmarkRankCohort;
	/** How many leaf items count toward the score right now. A cell holding fewer is why a reader sees `item-incomplete`. */
	scorableItemCount: number;
}

/**
 * Read only while the project actually has more than one item: a single-item project's cell table is its runs table
 * with extra indirection, and the runs query already polls that.
 */
export function useBenchmarkCells(projectId: string | null, enabled: boolean) {
	return useQuery<BenchmarkCellList>({
		queryKey: listBenchmarkCellsQueryKey({ path: { projectId: projectId ?? "" } }),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkCells({ path: { projectId: projectId as string }, signal });
			return {
				cells: (data.cells ?? []).map(toBenchmarkCell),
				cohort: toBenchmarkRankCohort(data.rankCohort),
				scorableItemCount: data.scorableItemCount ?? 0,
			};
		},
	});
}

/** Two to six cells side by side, plus every paired difference between them. */
export interface BenchmarkCellComparison extends BenchmarkCellList {
	/**
	 * One entry per unordered pair that shares at least three rankably-answered items. A REQUESTED pair with no entry
	 * means exactly that — too few shared items — and never a delta of zero.
	 */
	pairedDeltas: BenchmarkPairedDelta[];
}

/**
 * The paired-difference read. Enabled only for a real selection of two or more: the node refuses fewer, and asking it
 * anyway would turn an empty picker into an error the operator has to dismiss.
 */
export function useBenchmarkCellComparison(projectId: string | null, cellKeys: readonly string[]) {
	return useQuery<BenchmarkCellComparison>({
		// Keyed on the SORTED selection, so picking the same cells in another order reads the same entry.
		queryKey: compareBenchmarkCellsQueryKey({
			path: { projectId: projectId ?? "" },
			query: { cellKeys: [...cellKeys].sort() },
		}),
		enabled: Boolean(projectId) && cellKeys.length >= 2,
		queryFn: async ({ signal }) => {
			const { data } = await compareBenchmarkCells({
				path: { projectId: projectId as string },
				query: { cellKeys: [...cellKeys] },
				signal,
			});
			return {
				cells: (data.cells ?? []).map(toBenchmarkCell),
				cohort: toBenchmarkRankCohort(data.rankCohort),
				scorableItemCount: data.scorableItemCount ?? 0,
				pairedDeltas: (data.pairedDeltas ?? []).map(toBenchmarkPairedDelta),
			};
		},
	});
}
