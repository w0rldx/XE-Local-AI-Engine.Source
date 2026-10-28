import { type Query, useInfiniteQuery, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import type { ListBenchmarkRunsResponse } from "@/core/api/generated";
import {
	getBenchmarkKldDiskEstimate,
	getBenchmarkPairwiseEstimate,
	getBenchmarkProject,
	getBenchmarkRubricPresets,
	getBenchmarkRun,
	listBenchmarkComparisons,
	listBenchmarkProjects,
	listBenchmarkReviews,
	listEligibleBenchmarkModels,
} from "@/core/api/generated";
import {
	getBenchmarkKldDiskEstimateQueryKey,
	getBenchmarkPairwiseEstimateQueryKey,
	getBenchmarkProjectQueryKey,
	getBenchmarkRubricPresetsQueryKey,
	getBenchmarkRunQueryKey,
	listBenchmarkComparisonsQueryKey,
	listBenchmarkProjectsQueryKey,
	listBenchmarkReviewsQueryKey,
	listBenchmarkRunsInfiniteOptions,
	listEligibleBenchmarkModelsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { isFidelityActive } from "@/features/benchmarks/models/BenchmarkFidelity";
import {
	toBenchmarkComparisonList,
	toBenchmarkEligibleModel,
	toBenchmarkPairwiseEstimate,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRankCohort,
	toBenchmarkRubric,
	toBenchmarkRunDetail,
	toBenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkComparisonList,
	BenchmarkEligibleModel,
	BenchmarkPairwiseEstimate,
	BenchmarkProjectDetail,
	BenchmarkProjectSummary,
	BenchmarkRankCohort,
	BenchmarkReviewQueue,
	BenchmarkReviewQueueState,
	BenchmarkRubric,
	BenchmarkRunDetail,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkReviewPageSize, isJudgeActive, toBenchmarkReviewQueue } from "@/features/benchmarks/models/BenchmarkModels";

// Server state for the benchmarks surface. Calls the generated hey-api SDK fns DIRECTLY (the same imperative
// pattern as useLoadedModels / the chat adapter) rather than
// the generated TanStack `*Options()` wrappers, because several hooks below derive their poll cadence from the
// already-mapped domain data (`query.state.data`), which `select`-based options would not expose pre-mapping. The
// paged runs feed is the exception: it uses the generated `listBenchmarkRunsInfiniteOptions` and maps in `select`.
//
// Cache keys are the generated `*QueryKey` ones, so a partial key `[{ _id, path }]` matches every variant of one
// endpoint.

const activeRunPollIntervalMs = 2_000;
/** The largest page the node serves: `ListBenchmarkRunsEndpoint` answers 400 above it, so this is a ceiling, not a taste. */
const benchmarkRunsPageSize = 200;

/** The ranked runs of one project loaded so far: the rows plus what the ranking was computed against. */
export interface BenchmarkRunList {
	items: BenchmarkRunSummary[];
	cohort: BenchmarkRankCohort;
	/** Every run of the project, not just the loaded page — a matrix launch easily makes more than one page. */
	totalCount: number;
}

/**
 * The rubrics the node offers as starting points. `verifiable` is the one whose every criterion is decided
 * server-side, so a project judging under it spawns no llama-server at all.
 */
export interface BenchmarkRubricPresets {
	default: BenchmarkRubric | null;
	programming: BenchmarkRubric | null;
	reasoning: BenchmarkRubric | null;
	verifiable: BenchmarkRubric | null;
	/** Every criterion decided by running the answer's code against the operator's tests — no judge model at all. */
	codeExecution: BenchmarkRubric | null;
}

// Three things can still change a run's row, and the poll has to survive all three. Fidelity is the one that is easy to
// miss: it is measured on its own queue AFTER the primary and the judge are both terminal, so a predicate reading only
// those two stops polling the instant a measurement is queued and the finished numbers never arrive on their own.
export const isRunActive = (run: Pick<BenchmarkRunSummary, "primaryStatus" | "judge" | "fidelity">): boolean =>
	["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
	isJudgeActive(run.judge.state) ||
	isFidelityActive(run.fidelity);

function toBenchmarkRunList(page: ListBenchmarkRunsResponse): BenchmarkRunList {
	return {
		items: (page.items ?? []).map(toBenchmarkRunSummary),
		cohort: toBenchmarkRankCohort(page.rankCohort),
		totalCount: page.totalCount ?? (page.items ?? []).length,
	};
}

export function useBenchmarkProjects() {
	return useQuery<BenchmarkProjectSummary[]>({
		queryKey: listBenchmarkProjectsQueryKey(),
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkProjects({ signal });
			return (data.items ?? []).map(toBenchmarkProjectSummary);
		},
	});
}

export function useBenchmarkProject(projectId: string | null) {
	return useQuery<BenchmarkProjectDetail>({
		queryKey: getBenchmarkProjectQueryKey({ path: { projectId: projectId ?? "" } }),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await getBenchmarkProject({ path: { projectId: projectId as string }, signal });
			return toBenchmarkProjectDetail(data);
		},
	});
}

/**
 * The runs of one project, ranked, one page of 200 at a time. The node caps `pageSize` at 200 and a matrix launch can
 * make hundreds of runs, so the pages are appended rather than one page grown — and appending is safe here because the
 * ranking is computed project-wide by the node and the pages are contiguous slices of that one order, so the
 * concatenation is the same list the node would return in one go.
 *
 * The cost is that the two-second poll re-reads every loaded page, not just the first. That is one request per 200
 * runs, and the alternative is showing the operator half of a 400-run matrix.
 */
export function useBenchmarkRuns(projectId: string | null) {
	// A numeric page param overrides `query.page`; the page size and `includeUnscored` come from the key.
	const { queryKey, queryFn } = listBenchmarkRunsInfiniteOptions({
		path: { projectId: projectId ?? "" },
		query: { page: 1, pageSize: benchmarkRunsPageSize, includeUnscored: true },
	});
	const query = useInfiniteQuery({
		queryKey,
		queryFn,
		enabled: Boolean(projectId),
		initialPageParam: 1,
		getNextPageParam: (lastPage, pages) => {
			const loaded = pages.reduce((count, page) => count + (page.items ?? []).length, 0);
			// The empty-page guard is what stops a "load more" loop if the node ever reports a total it cannot serve.
			return (lastPage.items ?? []).length > 0 && loaded < toBenchmarkRunList(lastPage).totalCount ? pages.length + 1 : undefined;
		},
		refetchInterval: (query) =>
			query.state.data?.pages.some((page) => toBenchmarkRunList(page).items.some(isRunActive)) ? activeRunPollIntervalMs : false,
		// Flattened here so every consumer keeps seeing one ranked list and never the page machinery. Deduplicated by
		// id, first occurrence winning: the store pages by OFFSET over a newest-first order, so a run started while two
		// pages are loaded shifts every row down one and the next page re-serves the row that just left the previous
		// one. Un-deduplicated that is a repeated React key and one real run hidden behind its own copy.
		// simplified: keyset paging on (createdAtUtc, id) would remove the overlap itself rather than absorb it.
		select: (data): BenchmarkRunList => {
			const pages = data.pages.map(toBenchmarkRunList);
			return {
				items: [...new Map(pages.flatMap((page) => page.items).map((run) => [run.id, run])).values()],
				cohort: (pages[0] as BenchmarkRunList).cohort,
				totalCount: (pages[0] as BenchmarkRunList).totalCount,
			};
		},
	});
	// The promise is the caller's to ignore: a failed page read is already reported as the query's error state.
	return { ...query, loadMore: query.fetchNextPage };
}

// Shared by the single-run hook and the N-run one so both read through ONE cache entry per run: the compare view, the
// charts and the live pane all want the same run's detail, and three query keys for it would be three polls of it.
const benchmarkRunDetailQuery = (runId: string) => ({
	queryKey: getBenchmarkRunQueryKey({ path: { runId } }),
	// Case (c), active run only: the run hub streams into the live pane's overlay, never this cache, and the compare
	// view and charts read it with no hub mounted; status, scores, judge and fidelity results land here by refetch alone.
	refetchInterval: (query: { state: { data?: BenchmarkRunDetail } }) =>
		query.state.data && isRunActive(query.state.data) ? activeRunPollIntervalMs : (false as const),
	queryFn: async ({ signal }: { signal: AbortSignal }): Promise<BenchmarkRunDetail> => {
		const { data } = await getBenchmarkRun({ path: { runId }, signal });
		return toBenchmarkRunDetail(data);
	},
});

export function useBenchmarkRun(runId: string) {
	return useQuery<BenchmarkRunDetail>(benchmarkRunDetailQuery(runId));
}

/**
 * Several runs' details at once, in the order asked for. Nothing extra is fetched: the query keys are the ones
 * {@link useBenchmarkRun} already uses, so a run whose pane is open is read from that entry rather than re-requested.
 */
export function useBenchmarkRunDetails(runIds: readonly string[]) {
	return useQueries({
		queries: runIds.map((runId) => benchmarkRunDetailQuery(runId)),
		combine: (results) => ({
			runs: results.map((result) => result.data).filter((run): run is BenchmarkRunDetail => run !== undefined),
			isLoading: results.some((result) => result.isLoading),
		}),
	});
}

export function useEligibleBenchmarkModels(contextTokens?: number) {
	return useQuery<BenchmarkEligibleModel[]>({
		queryKey: listEligibleBenchmarkModelsQueryKey(contextTokens ? { query: { contextTokens } } : undefined),
		queryFn: async ({ signal }) => {
			const { data } = await listEligibleBenchmarkModels({ query: contextTokens ? { contextTokens } : undefined, signal });
			return (data.items ?? []).map(toBenchmarkEligibleModel);
		},
	});
}

/**
 * What enabling KL divergence would cost this project in disk. Read BEFORE the operator commits, never after: the base
 * logits are ~1.75 bytes per logit and 200 chunks of a 150k-vocabulary model is 25 GB, which is not a number to
 * discover once the cache write has already started.
 */
export interface BenchmarkKldDiskEstimate {
	estimatedBytes: number;
	freeDiskBytes: number;
	/** What the cache already holds for this base model — the estimate is not all new spend. */
	cachedBytes: number;
	chunks: number;
	contextTokens: number;
	vocabSize: number;
	/** The arithmetic, verbatim from the node, so the number is checkable rather than trusted. */
	formula: string;
	/** The node's own verdict on the reservation. Fail-closed: an absent flag reads as "does not fit". */
	fitsOnDisk: boolean;
}

/**
 * The KLD disk estimate for one project. `enabled` is the caller's: it is only worth asking while the operator is
 * actually looking at the fidelity settings, and the answer moves whenever the disk does.
 */
export function useBenchmarkKldDiskEstimate(projectId: string | null, chunks?: number, enabled = true) {
	return useQuery<BenchmarkKldDiskEstimate>({
		queryKey: getBenchmarkKldDiskEstimateQueryKey({
			path: { projectId: projectId ?? "" },
			...(chunks === undefined ? {} : { query: { chunks } }),
		}),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await getBenchmarkKldDiskEstimate({
				path: { projectId: projectId as string },
				...(chunks === undefined ? {} : { query: { chunks } }),
				signal,
			});
			return {
				estimatedBytes: data.estimatedBytes ?? 0,
				freeDiskBytes: data.freeDiskBytes ?? 0,
				cachedBytes: data.cachedBytes ?? 0,
				chunks: data.chunks ?? 0,
				contextTokens: data.contextTokens ?? 0,
				vocabSize: data.vocabSize ?? 0,
				formula: data.formula,
				fitsOnDisk: data.fitsOnDisk === true,
			};
		},
	});
}

/**
 * What the ranked reading of a pairwise project was computed from: which fit produced the scores, whether that fit still
 * describes the cohort, and which cohort/comparison set it was fitted over. Every one of those changes the number a run's
 * row shows; a verdict merely landing does not.
 */
const pairwiseFitSignature = (list: BenchmarkComparisonList): string =>
	[list.fit?.fitKey ?? "", list.fit?.isCurrent ?? false, list.comparisonSetVersion, list.cohortGeneration].join("|");

/**
 * The verdict matrix and the fit read out of it. Polls while any comparison is still working, for the same reason the
 * runs list does: a pairwise cohort finishes one pair at a time and the fit only appears after the last one.
 */
export function useBenchmarkComparisons(projectId: string | null, enabled = true) {
	const queryClient = useQueryClient();
	const queryKey = listBenchmarkComparisonsQueryKey({ path: { projectId: projectId ?? "" } });
	return useQuery<BenchmarkComparisonList>({
		queryKey,
		enabled: enabled && Boolean(projectId),
		refetchInterval: (query) =>
			query.state.data?.items.some((item) => item.status === "Queued" || item.status === "Running")
				? activeRunPollIntervalMs
				: false,
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkComparisons({ path: { projectId: projectId as string }, signal });
			const list = toBenchmarkComparisonList(data);
			// A pairwise project's scores and ranks are read out of the FIT, and this poll is the only thing watching for
			// one: the runs list polls on run activity, which a pairwise judging leaves untouched, so it would keep showing
			// null scores until something unrelated refetched it. Compared against what this cache already holds, so a poll
			// that merely advances the verdicts invalidates nothing — and the comparison of a first read against nothing
			// invalidates nothing either, since the list it would refresh was loaded from the same node state.
			// The project carries the cohort generation the judge panel shows, so it goes with them.
			const previous = queryClient.getQueryData<BenchmarkComparisonList>(queryKey);
			if (previous !== undefined && pairwiseFitSignature(previous) !== pairwiseFitSignature(list)) {
				// Not awaited: the verdicts are already in hand and must not wait on a second round trip. A failed refresh
				// is reported as that query's own error state, exactly as the paged `loadMore` above leaves it.
				// Never `projectScope` here: it matches this very query, whose refetch would see the same difference
				// again and invalidate again, forever.
				const path = { projectId: projectId as string };
				queryClient.invalidateQueries({ queryKey: [{ _id: "listBenchmarkRuns", path }] }).catch(() => undefined);
				queryClient.invalidateQueries({ queryKey: getBenchmarkProjectQueryKey({ path }) }).catch(() => undefined);
			}
			return list;
		},
	});
}

/**
 * What switching this project to pairwise would cost. Read BEFORE the save: 12 runs is 132 judge calls, and that is
 * not a number to discover once the queue is full.
 */
export function useBenchmarkPairwiseEstimate(projectId: string | null, enabled = true) {
	return useQuery<BenchmarkPairwiseEstimate>({
		queryKey: getBenchmarkPairwiseEstimateQueryKey({ path: { projectId: projectId ?? "" } }),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await getBenchmarkPairwiseEstimate({ path: { projectId: projectId as string }, signal });
			return toBenchmarkPairwiseEstimate(data);
		},
	});
}

/** The presets never change while the node runs, so they are read once and kept. */
export function useBenchmarkRubricPresets(enabled: boolean) {
	return useQuery<BenchmarkRubricPresets>({
		queryKey: getBenchmarkRubricPresetsQueryKey(),
		enabled,
		staleTime: Number.POSITIVE_INFINITY,
		queryFn: async ({ signal }) => {
			const { data } = await getBenchmarkRubricPresets({ signal });
			return {
				default: toBenchmarkRubric(data.default),
				programming: toBenchmarkRubric(data.programming),
				reasoning: toBenchmarkRubric(data.reasoning),
				verifiable: toBenchmarkRubric(data.verifiable),
				codeExecution: toBenchmarkRubric(data.codeExecution),
			};
		},
	});
}

/**
 * One page of the review inbox. No poll: nothing pushes review state, and every benchmark mutation already invalidates
 * the inbox; a judging that lands in the background shows up on the next read (the inbox's own refresh button).
 */
export function useBenchmarkReviews(
	state: BenchmarkReviewQueueState,
	projectId: string | null,
	page: number,
	take: number = benchmarkReviewPageSize.default,
) {
	const query = { state, ...(projectId === null ? {} : { projectId }), page, take };
	return useQuery<BenchmarkReviewQueue>({
		queryKey: listBenchmarkReviewsQueryKey({ query }),
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkReviews({ query, signal });
			return toBenchmarkReviewQueue(data);
		},
	});
}

// A run's DETAIL query polls only while that run is active, so a change made elsewhere (a project-wide judge change
// or re-judge) can never be picked up by an open detail pane on its own: its cached copy says the run is finished, so
// nothing schedules another read. Every mutation therefore names the runs it touched.
//
// Everything else is the project list plus every project-addressed query of every project: details, runs, cells,
// items (a freeze adds cells, an item edit unranks the answers to the old question), estimates and comparisons — and
// the review inbox, which spans projects and changes with a judging, a policy change or a review alike.
const isProjectQuery = (query: Query): boolean => {
	const key = query.queryKey[0] as { _id?: string; tags?: readonly string[]; path?: { projectId?: string } } | undefined;
	return (
		key?.tags?.includes("Benchmarks") === true &&
		(key._id === "listBenchmarkProjects" || key._id === "listBenchmarkReviews" || key.path?.projectId !== undefined)
	);
};

export function useBenchmarkInvalidation() {
	const queryClient = useQueryClient();
	return async (...runIds: readonly string[]): Promise<void> => {
		await queryClient.invalidateQueries({ predicate: isProjectQuery });
		await Promise.all(
			runIds.map((runId) => queryClient.invalidateQueries({ queryKey: getBenchmarkRunQueryKey({ path: { runId } }) })),
		);
	};
}
