import { type Query, useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import type {
	ListBenchmarkRunsResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunBatchItem as StartBenchmarkRunBatchItem,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunRequest as StartBenchmarkRunRequest,
} from "@/core/api/generated";
import {
	cancelBenchmarkRun,
	clearBenchmarkFidelityCache,
	clearBenchmarkRunScore,
	compareBenchmarkCells,
	createBenchmarkProject,
	createBenchmarkTaskItem,
	deleteBenchmarkProject,
	deleteBenchmarkRun,
	deleteBenchmarkTaskItem,
	getBenchmarkKldDiskEstimate,
	getBenchmarkPairwiseEstimate,
	getBenchmarkProject,
	getBenchmarkRubricPresets,
	getBenchmarkRun,
	getBenchmarkRunArtifact,
	listBenchmarkCells,
	listBenchmarkComparisons,
	listBenchmarkProjects,
	listBenchmarkReviews,
	listBenchmarkTaskItems,
	listEligibleBenchmarkModels,
	rejudgeBenchmarkProject,
	rejudgeBenchmarkRun,
	reorderBenchmarkTaskItems,
	scoreBenchmarkRun,
	startBenchmarkRun,
	startBenchmarkRunBatch,
	startBenchmarkRunFidelity,
	submitBenchmarkRunReview,
	updateBenchmarkJudgePolicy,
	updateBenchmarkProject,
	updateBenchmarkProjectFidelity,
	updateBenchmarkTaskItem,
} from "@/core/api/generated";
import {
	compareBenchmarkCellsQueryKey,
	getBenchmarkKldDiskEstimateQueryKey,
	getBenchmarkPairwiseEstimateQueryKey,
	getBenchmarkProjectQueryKey,
	getBenchmarkRubricPresetsQueryKey,
	getBenchmarkRunQueryKey,
	listBenchmarkCellsQueryKey,
	listBenchmarkComparisonsQueryKey,
	listBenchmarkProjectsQueryKey,
	listBenchmarkReviewsQueryKey,
	listBenchmarkRunsInfiniteOptions,
	listBenchmarkTaskItemsQueryKey,
	listEligibleBenchmarkModelsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { fileNameFromContentDisposition, saveBlob } from "@/core/api/utils/DownloadBlob";
import type { BenchmarkCell, BenchmarkPairedDelta } from "@/features/benchmarks/models/BenchmarkCells";
import { toBenchmarkCell, toBenchmarkPairedDelta } from "@/features/benchmarks/models/BenchmarkCells";
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
	BenchmarkKvCacheType,
	BenchmarkPairwiseEstimate,
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkProjectFidelityDraft,
	BenchmarkProjectSummary,
	BenchmarkRankCohort,
	BenchmarkRepeatMode,
	BenchmarkReviewQueue,
	BenchmarkReviewQueueState,
	BenchmarkReviewSubmission,
	BenchmarkRubric,
	BenchmarkRunArtifact,
	BenchmarkRunDetail,
	BenchmarkRunRef,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkReviewPageSize, isJudgeActive, toBenchmarkReviewQueue } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkTaskItem, BenchmarkTaskItemDraft } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { pruneVerifierOverrides, toBenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";

// Server state for the benchmarks surface. Calls the generated hey-api SDK fns DIRECTLY (the same imperative
// pattern as useLoadedModels / the chat adapter) rather than
// the generated TanStack `*Options()` wrappers, because several hooks below derive their poll cadence from the
// already-mapped domain data (`query.state.data`), which `select`-based options would not expose pre-mapping. The
// paged runs feed is the exception: it uses the generated `listBenchmarkRunsInfiniteOptions` and maps in `select`.
//
// Cache keys are the generated `*QueryKey` ones, so a partial key `[{ _id, path }]` matches every variant of one
// endpoint. `projectScope` matches every query addressed to one project, whatever its endpoint.
const projectScope = (projectId: string) => [{ tags: ["Benchmarks"], path: { projectId } }] as const;

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

function useBenchmarkInvalidation() {
	const queryClient = useQueryClient();
	return async (...runIds: readonly string[]): Promise<void> => {
		await queryClient.invalidateQueries({ predicate: isProjectQuery });
		await Promise.all(
			runIds.map((runId) => queryClient.invalidateQueries({ queryKey: getBenchmarkRunQueryKey({ path: { runId } }) })),
		);
	};
}

// A null rubric/referenceAnswer is "the node decides" (default rubric, no reference answer): the member is omitted
// rather than sent as null, so an unset field can never be mistaken for a deliberate blanking.
const projectMutationBody = (draft: BenchmarkProjectDraft) => ({
	name: draft.name,
	coreTask: draft.coreTask,
	contextTokens: draft.contextTokens,
	// Omitted rather than sent as null when unset: an absent budget means context-limited, which the node validates
	// against the context window.
	...(draft.maxOutputTokens === null ? {} : { maxOutputTokens: draft.maxOutputTokens }),
	...(draft.reasoningBudgetTokens === null ? {} : { reasoningBudgetTokens: draft.reasoningBudgetTokens }),
	...(draft.invocationTimeoutSeconds === null ? {} : { invocationTimeoutSeconds: draft.invocationTimeoutSeconds }),
	agentDefinitionId: draft.agentDefinitionId,
	judgeEnabled: draft.judgeEnabled,
	// `judgeMode` is deliberately NOT here: the project write does not carry it, only `PUT .../judge` does. That is
	// also the only path where the mode matters — pairwise needs runs to compare, and a project with runs is frozen,
	// which is exactly when the judge-policy route is the one used.
	judgeModelName: draft.judgeModelName,
	judgeContextTokens: draft.judgeContextTokens,
	...(draft.rubric === null ? {} : { rubric: draft.rubric }),
	...(draft.referenceAnswer === null ? {} : { referenceAnswer: draft.referenceAnswer }),
	fidelityEnabled: draft.fidelityEnabled,
	fidelityKldEnabled: draft.fidelityKldEnabled,
	// Omitted rather than null when unset, exactly as the token budgets above: absent means "the node's default", and
	// the node clamps what it accepts. `fidelityKldBaseFingerprint` is deliberately NOT sent — the node resolves it
	// from the base model, and a caller-supplied one could make two incomparable figures compare equal.
	...(draft.fidelityChunks === null ? {} : { fidelityChunks: draft.fidelityChunks }),
	fidelityKldBaseModelName: draft.fidelityKldBaseModelName,
	requiresReview: draft.requiresReview,
});

export function useCreateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (draft: BenchmarkProjectDraft) => {
			const { data } = await createBenchmarkProject({ body: projectMutationBody(draft) });
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useUpdateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			draft,
		}: {
			projectId: string;
			expectedVersion: number;
			draft: BenchmarkProjectDraft;
		}) => {
			const { data } = await updateBenchmarkProject({
				path: { projectId },
				body: { ...projectMutationBody(draft), expectedVersion },
			});
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: () => invalidate(),
	});
}

/**
 * Deletes a project with everything under it: its runs and all their results, transcripts and judge verdicts, then its
 * task items, its pairwise fits and its judge-policy history.
 *
 * The node refuses with 409 `ActiveRun` — having deleted nothing — while any of the project's runs is still queued,
 * generating, judging or being compared. Finished runs are deleted with the project; a live one blocks the whole call,
 * so this can never race the run queue. Nothing outside the benchmarks tables points at a project.
 *
 * The invalidation is deliberately `exact`, unlike every other mutation here: the shared {@link useBenchmarkInvalidation}
 * re-reads every project-addressed query, and for a project that no longer exists that is a
 * guaranteed 404 refetch of its own detail while the page still has it selected. Only the LIST is re-read; the page
 * controller then sees the project leave it and selects another one on its own. The deleted project's own entries are
 * dropped from the cache afterwards, once the refreshed list has already moved the selection off it.
 */
export function useDeleteBenchmarkProject() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async ({ projectId, expectedVersion }: { projectId: string; expectedVersion: number }) => {
			await deleteBenchmarkProject({ path: { projectId }, body: { expectedVersion } });
		},
		onSuccess: async (_, { projectId }) => {
			await queryClient.invalidateQueries({ queryKey: listBenchmarkProjectsQueryKey(), exact: true });
			// Scope removal: the project's detail, runs, task items, cells, comparisons and estimates all carry its id
			// in `path`. `inactive` only, because the page may not have re-rendered off the deleted project yet and
			// removing a query that still has an observer makes it refetch — a guaranteed 404 for a project that is
			// gone. What is still mounted becomes inactive on that re-render and is collected with the rest.
			queryClient.removeQueries({ queryKey: projectScope(projectId), type: "inactive" });
		},
	});
}

/** What a judge change did: the refreshed project plus the runs it enqueued for re-judging. */
export interface BenchmarkJudgeChange {
	project: BenchmarkProjectDetail;
	enqueuedRunIds: readonly string[];
	enqueuedRunCount: number;
}

/**
 * The judge policy of a project, editable even while the project is frozen (its task/agent/context are not). A change
 * that would re-score existing runs is refused with 409 `RejudgeRequired` until `confirmRejudge` is set — the caller
 * asks the operator and resends.
 */
export function useUpdateBenchmarkJudgePolicy() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			policy,
			confirmRejudge,
		}: {
			projectId: string;
			expectedVersion: number;
			/** null disables judging; existing attempts and revisions stay as history. */
			policy: JudgePolicyDraft | null;
			confirmRejudge: boolean;
		}) => {
			const { data } = await updateBenchmarkJudgePolicy({
				path: { projectId },
				body: { policy, expectedVersion, confirmRejudge },
			});
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(...change.enqueuedRunIds),
	});
}

/** What a fidelity change did: the refreshed project plus the runs it queued a measurement for. */
export interface BenchmarkFidelityChange {
	project: BenchmarkProjectDetail;
	enqueuedCount: number;
}

/**
 * The fidelity settings on their OWN route, with their own CAS. That is what lets them change on a frozen project:
 * the ordinary project write is refused once runs exist, and fidelity is exactly the setting an operator wants to
 * revisit after seeing some.
 *
 * `measureExisting` is opt-in because enabling fidelity should not silently spend GPU on a project's whole history.
 * Pressing it twice queues nothing the second time — runs with an attempt already are skipped by the node.
 */
export function useUpdateBenchmarkProjectFidelity() {
	const invalidate = useBenchmarkInvalidation();
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedVersion,
			draft,
			measureExisting,
		}: {
			projectId: string;
			expectedVersion: number;
			draft: BenchmarkProjectFidelityDraft;
			measureExisting: boolean;
		}): Promise<BenchmarkFidelityChange> => {
			const { data } = await updateBenchmarkProjectFidelity({
				path: { projectId },
				body: {
					expectedVersion,
					fidelityEnabled: draft.fidelityEnabled,
					fidelityKldEnabled: draft.fidelityKldEnabled,
					// Omitted rather than null when unset, so an absent value means the node's default.
					...(draft.fidelityChunks === null ? {} : { fidelityChunks: draft.fidelityChunks }),
					fidelityKldBaseModelName: draft.fidelityKldBaseModelName,
					measureExisting,
				},
			});
			return { project: toBenchmarkProjectDetail(data.project), enqueuedCount: data.enqueuedCount ?? 0 };
		},
		// The refreshed runs carry their measurement as `queued`, which is what puts the list back on the two-second poll.
		// The response counts the runs it enqueued but does not name them, so an OPEN detail pane — whose own query stopped
		// polling when the run went terminal — is reached by invalidating the run details as a family. Only when something
		// was actually enqueued: a save that measured nothing must not sweep every open pane.
		onSuccess: async (change) => {
			await invalidate();
			if (change.enqueuedCount > 0) {
				await queryClient.invalidateQueries({ queryKey: [{ _id: "getBenchmarkRun" }] });
			}
		},
	});
}

export function useRejudgeBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ projectId, expectedVersion }: { projectId: string; expectedVersion: number }) => {
			const { data } = await rejudgeBenchmarkProject({ path: { projectId }, body: { expectedVersion } });
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(...change.enqueuedRunIds),
	});
}

export function useStartBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			modelName,
			expectedProjectVersion,
			kvCacheType,
			agentDefinitionId,
			repeatMode,
			answerVarianceTemperature,
		}: {
			projectId: string;
			modelName: string;
			expectedProjectVersion: number;
			/** null = Auto: the member is omitted so the node applies its own rule at freeze. */
			kvCacheType: BenchmarkKvCacheType | null;
			/** An agent variant to run instead of the project's agent; omitted = the project's agent. */
			agentDefinitionId?: string;
			repeatMode: BenchmarkRepeatMode;
			/** null = the node's default (0.7). Never sent in throughput mode, which the node samples at 0. */
			answerVarianceTemperature: number | null;
		}) => {
			const body: StartBenchmarkRunRequest = {
				modelName,
				expectedProjectVersion,
				...(kvCacheType === null ? {} : { kvCacheType }),
				...(agentDefinitionId === undefined ? {} : { agentDefinitionId }),
				repeatMode,
				...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
			};
			const { data } = await startBenchmarkRun({ path: { projectId }, body });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/** One matrix cell the node refused, in the same `code` vocabulary a single-run failure uses. */
export interface BenchmarkBatchRejection {
	modelName: string;
	kvCacheType: string | null;
	/** The variant the refused cell asked for; null = the project's own agent. */
	agentDefinitionId: string | null;
	code: string;
	message: string;
}

/** What a batch launch did: the cells that started, and the ones that did not with the reason. */
export interface BenchmarkBatchLaunch {
	startedRunIds: string[];
	rejected: BenchmarkBatchRejection[];
}

/**
 * Launches a whole model × KV-type matrix in one request. Deliberately NOT all-or-nothing — the node reports each cell
 * separately, so an ineligible model comes back as one rejection in a 200 rather than as a failure of the batch. Only a
 * stale project version or a vanished project fails the whole call.
 */
export function useStartBenchmarkRunBatch() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedProjectVersion,
			items,
			repeatCount,
			warmup,
			repeatMode,
			answerVarianceTemperature,
		}: {
			projectId: string;
			expectedProjectVersion: number;
			items: StartBenchmarkRunBatchItem[];
			repeatCount: number;
			warmup: boolean;
			repeatMode: BenchmarkRepeatMode;
			answerVarianceTemperature: number | null;
		}): Promise<BenchmarkBatchLaunch & { projectId: string }> => {
			const { data } = await startBenchmarkRunBatch({
				path: { projectId },
				body: {
					expectedProjectVersion,
					items,
					repeatCount,
					warmup,
					repeatMode,
					...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
				},
			});
			return {
				projectId,
				startedRunIds: (data.started ?? []).flatMap((item) => item.runIds ?? []),
				rejected: (data.rejected ?? []).map((item) => ({
					modelName: item.modelName,
					kvCacheType: item.kvCacheType ?? null,
					agentDefinitionId: item.agentDefinitionId ?? null,
					code: item.code,
					message: item.message,
				})),
			};
		},
		onSuccess: (result) => invalidate(...result.startedRunIds),
	});
}

export function useCancelBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, target }: { run: BenchmarkRunRef; target: "Primary" | "Judge" }) => {
			const { data } = await cancelBenchmarkRun({ path: { runId: run.id }, body: { target, expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

export function useScoreBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, score }: { run: BenchmarkRunRef; score: number }) => {
			const { data } = await scoreBenchmarkRun({ path: { runId: run.id }, body: { score, expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/** Drops the operator override so the run falls back to its judge score (or to unscored). */
export function useClearBenchmarkRunScore() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			const { data } = await clearBenchmarkRunScore({ path: { runId: run.id }, body: { expectedVersion: run.version } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

export function useRejudgeBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, force }: { run: BenchmarkRunRef; force: boolean }) => {
			const { data } = await rejudgeBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version, force } });
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
	});
}

/**
 * A reviewer's decision on one scoring run of a review project; the latest one wins. The node refuses with 409
 * `NotReviewable` while the judging has not succeeded under the current policy, and with `VersionConflict` when the run
 * moved since it was read.
 */
export function useSubmitBenchmarkRunReview() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, review }: { run: BenchmarkRunRef; review: BenchmarkReviewSubmission }) => {
			const { data } = await submitBenchmarkRunReview({
				path: { runId: run.id },
				body: { ...review, expectedVersion: run.version },
			});
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.id),
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

/**
 * Downloads one verifier artifact. The bytes are the model's own code output, so they are saved as a text file and
 * never rendered: the `blob` override keeps them out of the DOM exactly as the project export does.
 */
export function useBenchmarkRunArtifact() {
	return useMutation({
		mutationFn: async ({ runId, artifact }: { runId: string; artifact: Pick<BenchmarkRunArtifact, "id" | "kind"> }) => {
			const response = await getBenchmarkRunArtifact({ path: { runId, artifactId: artifact.id }, responseType: "blob" });
			saveBlob(
				// The blob override changes the body axios returns, not the generated response type.
				response.data as unknown as Blob,
				fileNameFromContentDisposition(response.headers["content-disposition"]) ?? `${artifact.kind}-${artifact.id}.txt`,
			);
		},
	});
}

/**
 * Re-measures one run's quant fidelity. The node inserts a NEW immutable attempt rather than overwriting: the previous
 * numbers survive a failed re-measure, which is why this is safe to offer as a plain menu action.
 */
export function useStartBenchmarkRunFidelity() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			await startBenchmarkRunFidelity({ path: { runId: run.id } });
		},
		onSuccess: (_, run) => invalidate(run.id),
	});
}

/**
 * Drops this project's cached base logits. Nothing measured is lost — the cache is a derived file, and the runs keep
 * the numbers that were computed from it — but the next KLD measurement pays the full base pass again.
 */
export function useClearBenchmarkFidelityCache() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (projectId: string) => {
			await clearBenchmarkFidelityCache({ path: { projectId } });
		},
		onSuccess: () => invalidate(),
	});
}

export function useDeleteBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			await deleteBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version } });
		},
		onSuccess: (_, run) => invalidate(run.id),
	});
}

// Task-item writes change the item-set hash; invalidate runs/cells and warn the operator before mutation.

/** One project's task items, in index order, with the set hash the node currently computes over them. */
export interface BenchmarkTaskItemList {
	items: BenchmarkTaskItem[];
	/** Null until the first item write. A run stamped with a different one is excluded as `item-set-revised`. */
	taskItemSetHash: string | null;
	/** The version an item CREATE must be made against — adding an item changes what a freeze would produce. */
	projectVersion: number;
}

export function useBenchmarkTaskItems(projectId: string | null) {
	return useQuery<BenchmarkTaskItemList>({
		queryKey: listBenchmarkTaskItemsQueryKey({ path: { projectId: projectId ?? "" } }),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await listBenchmarkTaskItems({ path: { projectId: projectId as string }, signal });
			return {
				items: (data.items ?? []).map(toBenchmarkTaskItem).sort((left, right) => left.index - right.index),
				taskItemSetHash: data.taskItemSetHash ?? null,
				projectVersion: data.projectVersion ?? 0,
			};
		},
	});
}

// The generator members are omitted rather than sent as null when unset, exactly as the project body does it: an
// absent blob is "this item has none", and a present empty one is a blob the node has to parse and refuse.
const taskItemMutationBody = (draft: BenchmarkTaskItemDraft) => ({
	prompt: draft.prompt,
	kind: draft.kind,
	countsTowardScore: draft.countsTowardScore,
	...(draft.referenceAnswer === null ? {} : { referenceAnswer: draft.referenceAnswer }),
	...(pruneVerifierOverrides(draft.verifierConfig) === null
		? {}
		: { verifierConfig: pruneVerifierOverrides(draft.verifierConfig) }),
	...(draft.generatorConfig === null ? {} : { generatorConfig: draft.generatorConfig }),
});

export function useCreateBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			expectedProjectVersion,
			draft,
		}: {
			projectId: string;
			expectedProjectVersion: number;
			draft: BenchmarkTaskItemDraft;
		}) => {
			const { data } = await createBenchmarkTaskItem({
				path: { projectId },
				body: { ...taskItemMutationBody(draft), expectedProjectVersion },
			});
			return toBenchmarkTaskItem(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useUpdateBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({
			projectId,
			item,
			draft,
		}: {
			projectId: string;
			/** The ITEM's version, not the project's — an edit is a write to one item. */
			item: Pick<BenchmarkTaskItem, "id" | "version">;
			draft: BenchmarkTaskItemDraft;
		}) => {
			const { data } = await updateBenchmarkTaskItem({
				path: { projectId, itemId: item.id },
				body: { ...taskItemMutationBody(draft), expectedVersion: item.version },
			});
			return toBenchmarkTaskItem(data);
		},
		onSuccess: () => invalidate(),
	});
}

export function useDeleteBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: ({ projectId, item }: { projectId: string; item: Pick<BenchmarkTaskItem, "id" | "version"> }) =>
			deleteBenchmarkTaskItem({
				path: { projectId, itemId: item.id },
				body: { expectedVersion: item.version },
			}),
		onSuccess: () => invalidate(),
	});
}

/**
 * The whole new order at once. Naming every current id IS the concurrency check — an item added or deleted while the
 * operator was dragging makes the two sets disagree and the node refuses — so there is no version token to send.
 */
export function useReorderBenchmarkTaskItems() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ projectId, itemIds }: { projectId: string; itemIds: readonly string[] }) => {
			const { data } = await reorderBenchmarkTaskItems({ path: { projectId }, body: { itemIds: [...itemIds] } });
			return (data.items ?? []).map(toBenchmarkTaskItem).sort((left, right) => left.index - right.index);
		},
		onSuccess: () => invalidate(),
	});
}

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
