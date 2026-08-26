import { useInfiniteQuery, useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelBenchmarkRun,
	clearBenchmarkFidelityCache,
	clearBenchmarkRunScore,
	createBenchmarkProject,
	createBenchmarkTaskItem,
	deleteBenchmarkRun,
	deleteBenchmarkTaskItem,
	getBenchmarkKldDiskEstimate,
	getBenchmarkPairwiseEstimate,
	getBenchmarkProject,
	getBenchmarkRubricPresets,
	getBenchmarkRun,
	compareBenchmarkCells,
	listBenchmarkCells,
	listBenchmarkComparisons,
	listBenchmarkProjects,
	listBenchmarkRuns,
	listBenchmarkTaskItems,
	listEligibleBenchmarkModels,
	rejudgeBenchmarkProject,
	rejudgeBenchmarkRun,
	reorderBenchmarkTaskItems,
	scoreBenchmarkRun,
	startBenchmarkRun,
	startBenchmarkRunBatch,
	startBenchmarkRunFidelity,
	updateBenchmarkJudgePolicy,
	updateBenchmarkProject,
	updateBenchmarkProjectFidelity,
	updateBenchmarkTaskItem,
} from "@/core/api/generated";
import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunBatchItem as StartBenchmarkRunBatchItem,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunRequest as StartBenchmarkRunRequest,
} from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
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
	BenchmarkProjectFidelityDraft,
	BenchmarkEligibleModel,
	BenchmarkPairwiseEstimate,
	BenchmarkKvCacheType,
	BenchmarkRepeatMode,
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkProjectSummary,
	BenchmarkRankCohort,
	BenchmarkRubric,
	BenchmarkRunDetail,
	BenchmarkRunRef,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { isJudgeActive } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkTaskItem, BenchmarkTaskItemDraft } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { pruneVerifierOverrides, toBenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";

// Server state for the benchmarks surface. Calls the generated hey-api SDK fns DIRECTLY through the shared
// `callWithResponseValidation` bridge (the same imperative pattern as useLoadedModels / the chat adapter) rather than
// the generated TanStack `*Options()` wrappers, because several hooks below derive their poll cadence from the
// already-mapped domain data (`query.state.data`), which `select`-based options would not expose pre-mapping.

const benchmarkQueryKeys = {
	projects: ["benchmarks", "projects"] as const,
	project: (id: string) => ["benchmarks", "projects", id] as const,
	runs: (projectId: string) => ["benchmarks", "projects", projectId, "runs"] as const,
	taskItems: (projectId: string) => ["benchmarks", "projects", projectId, "items"] as const,
	cells: (projectId: string) => ["benchmarks", "projects", projectId, "cells"] as const,
	cellComparison: (projectId: string, cellKeys: readonly string[]) =>
		["benchmarks", "projects", projectId, "compare", [...cellKeys].sort().join("|")] as const,
	run: (id: string) => ["benchmarks", "runs", id] as const,
	/** The prefix every {@link benchmarkQueryKeys.run} entry sits under, for the rare change that touches runs it cannot name. */
	runDetails: ["benchmarks", "runs"] as const,
	models: (contextTokens?: number) => ["benchmarks", "eligible-models", contextTokens] as const,
	kldEstimate: (projectId: string, chunks?: number) => ["benchmarks", "projects", projectId, "kld-estimate", chunks] as const,
	comparisons: (projectId: string) => ["benchmarks", "projects", projectId, "comparisons"] as const,
	pairwiseEstimate: (projectId: string) => ["benchmarks", "projects", projectId, "pairwise-estimate"] as const,
	rubricPresets: ["benchmarks", "rubric-presets"] as const,
};

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
const isRunActive = (run: Pick<BenchmarkRunSummary, "primaryStatus" | "judge" | "fidelity">): boolean =>
	["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
	isJudgeActive(run.judge.state) ||
	isFidelityActive(run.fidelity);

function hasActiveRun(list: BenchmarkRunList | undefined): boolean {
	return list?.items.some(isRunActive) ?? false;
}

export function useBenchmarkProjects() {
	return useQuery<BenchmarkProjectSummary[]>({
		queryKey: benchmarkQueryKeys.projects,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(listBenchmarkProjects({ signal, throwOnError: true }));
			return (data.items ?? []).map(toBenchmarkProjectSummary);
		},
	});
}

export function useBenchmarkProject(projectId: string | null) {
	return useQuery<BenchmarkProjectDetail>({
		queryKey: benchmarkQueryKeys.project(projectId ?? ""),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				getBenchmarkProject({ path: { projectId: projectId as string }, signal, throwOnError: true }),
			);
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
	const query = useInfiniteQuery({
		queryKey: benchmarkQueryKeys.runs(projectId ?? ""),
		enabled: Boolean(projectId),
		initialPageParam: 1,
		getNextPageParam: (lastPage: BenchmarkRunList, pages: BenchmarkRunList[]) => {
			const loaded = pages.reduce((count, page) => count + page.items.length, 0);
			// The empty-page guard is what stops a "load more" loop if the node ever reports a total it cannot serve.
			return lastPage.items.length > 0 && loaded < lastPage.totalCount ? pages.length + 1 : undefined;
		},
		refetchInterval: (query) => (query.state.data?.pages.some(hasActiveRun) ? activeRunPollIntervalMs : false),
		queryFn: async ({ pageParam, signal }): Promise<BenchmarkRunList> => {
			const { data } = await callWithResponseValidation(
				listBenchmarkRuns({
					path: { projectId: projectId as string },
					query: { page: pageParam, pageSize: benchmarkRunsPageSize, includeUnscored: true },
					signal,
					throwOnError: true,
				}),
			);
			return {
				items: (data.items ?? []).map(toBenchmarkRunSummary),
				cohort: toBenchmarkRankCohort(data.rankCohort),
				totalCount: data.totalCount ?? (data.items ?? []).length,
			};
		},
		// Flattened here so every consumer keeps seeing one ranked list and never the page machinery. Deduplicated by
		// id, first occurrence winning: the store pages by OFFSET over a newest-first order, so a run started while two
		// pages are loaded shifts every row down one and the next page re-serves the row that just left the previous
		// one. Un-deduplicated that is a repeated React key and one real run hidden behind its own copy.
		// ponytail: keyset paging on (createdAtUtc, id) would remove the overlap itself rather than absorb it.
		select: (data) => ({
			items: [...new Map(data.pages.flatMap((page) => page.items).map((run) => [run.id, run])).values()],
			cohort: (data.pages[0] as BenchmarkRunList).cohort,
			totalCount: (data.pages[0] as BenchmarkRunList).totalCount,
		}),
	});
	// The promise is the caller's to ignore: a failed page read is already reported as the query's error state.
	return { ...query, loadMore: query.fetchNextPage };
}

// Shared by the single-run hook and the N-run one so both read through ONE cache entry per run: the compare view, the
// charts and the live pane all want the same run's detail, and three query keys for it would be three polls of it.
const benchmarkRunDetailQuery = (runId: string) => ({
	queryKey: benchmarkQueryKeys.run(runId),
	refetchInterval: (query: { state: { data?: BenchmarkRunDetail } }) =>
		query.state.data && isRunActive(query.state.data) ? activeRunPollIntervalMs : (false as const),
	queryFn: async ({ signal }: { signal: AbortSignal }): Promise<BenchmarkRunDetail> => {
		const { data } = await callWithResponseValidation(getBenchmarkRun({ path: { runId }, signal, throwOnError: true }));
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
		queryKey: benchmarkQueryKeys.models(contextTokens),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listEligibleBenchmarkModels({ query: contextTokens ? { contextTokens } : undefined, signal, throwOnError: true }),
			);
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
		queryKey: benchmarkQueryKeys.kldEstimate(projectId ?? "", chunks),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				getBenchmarkKldDiskEstimate({
					path: { projectId: projectId as string },
					...(chunks === undefined ? {} : { query: { chunks } }),
					signal,
					throwOnError: true,
				}),
			);
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
	const queryKey = benchmarkQueryKeys.comparisons(projectId ?? "");
	return useQuery<BenchmarkComparisonList>({
		queryKey,
		enabled: enabled && Boolean(projectId),
		refetchInterval: (query) =>
			query.state.data?.items.some((item) => item.status === "Queued" || item.status === "Running")
				? activeRunPollIntervalMs
				: false,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listBenchmarkComparisons({ path: { projectId: projectId as string }, signal, throwOnError: true }),
			);
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
				// `exact` on the project is load-bearing: its key is a PREFIX of this very query's key, so a prefix
				// invalidation would refetch the comparisons too — and that refetch would see the same difference again
				// and invalidate again, forever.
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.runs(projectId as string) }).catch(() => undefined);
				queryClient
					.invalidateQueries({ queryKey: benchmarkQueryKeys.project(projectId as string), exact: true })
					.catch(() => undefined);
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
		queryKey: benchmarkQueryKeys.pairwiseEstimate(projectId ?? ""),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				getBenchmarkPairwiseEstimate({ path: { projectId: projectId as string }, signal, throwOnError: true }),
			);
			return toBenchmarkPairwiseEstimate(data);
		},
	});
}

/** The presets never change while the node runs, so they are read once and kept. */
export function useBenchmarkRubricPresets(enabled: boolean) {
	return useQuery<BenchmarkRubricPresets>({
		queryKey: benchmarkQueryKeys.rubricPresets,
		enabled,
		staleTime: Number.POSITIVE_INFINITY,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getBenchmarkRubricPresets({ signal, throwOnError: true }));
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
// nothing schedules another read. Every mutation therefore names the runs it touched, not just the project.
function useBenchmarkInvalidation() {
	const queryClient = useQueryClient();
	return async (projectId?: string, ...runIds: readonly string[]): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.projects });
		if (projectId) {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.project(projectId) }),
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.runs(projectId) }),
				// The cell table and the item list are re-read for the same reason the runs are: a freeze adds cells, an
				// item edit unranks the ones that answered the old question, and neither is visible in a run row alone.
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.cells(projectId) }),
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.taskItems(projectId) }),
			]);
		}
		await Promise.all(runIds.map((runId) => queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.run(runId) })));
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
});

export function useCreateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (draft: BenchmarkProjectDraft) => {
			const { data } = await callWithResponseValidation(
				createBenchmarkProject({ body: projectMutationBody(draft), throwOnError: true }),
			);
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: (project) => invalidate(project.id),
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
			const { data } = await callWithResponseValidation(
				updateBenchmarkProject({
					path: { projectId },
					body: { ...projectMutationBody(draft), expectedVersion },
					throwOnError: true,
				}),
			);
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: (project) => invalidate(project.id),
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
			const { data } = await callWithResponseValidation(
				updateBenchmarkJudgePolicy({
					path: { projectId },
					body: { policy, expectedVersion, confirmRejudge },
					throwOnError: true,
				}),
			);
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(change.project.id, ...change.enqueuedRunIds),
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
			const { data } = await callWithResponseValidation(
				updateBenchmarkProjectFidelity({
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
					throwOnError: true,
				}),
			);
			return { project: toBenchmarkProjectDetail(data.project), enqueuedCount: data.enqueuedCount ?? 0 };
		},
		// The refreshed runs carry their measurement as `queued`, which is what puts the list back on the two-second poll.
		// The response counts the runs it enqueued but does not name them, so an OPEN detail pane — whose own query stopped
		// polling when the run went terminal — is reached by invalidating the run details as a family. Only when something
		// was actually enqueued: a save that measured nothing must not sweep every open pane.
		onSuccess: async (change) => {
			await invalidate(change.project.id);
			if (change.enqueuedCount > 0) {
				await queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.runDetails });
			}
		},
	});
}

export function useRejudgeBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ projectId, expectedVersion }: { projectId: string; expectedVersion: number }) => {
			const { data } = await callWithResponseValidation(
				rejudgeBenchmarkProject({ path: { projectId }, body: { expectedVersion }, throwOnError: true }),
			);
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(change.project.id, ...change.enqueuedRunIds),
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
			repeatMode,
			answerVarianceTemperature,
		}: {
			projectId: string;
			modelName: string;
			expectedProjectVersion: number;
			/** null = Auto: the member is omitted so the node applies its own rule at freeze. */
			kvCacheType: BenchmarkKvCacheType | null;
			repeatMode: BenchmarkRepeatMode;
			/** null = the node's default (0.7). Never sent in throughput mode, which the node samples at 0. */
			answerVarianceTemperature: number | null;
		}) => {
			const body: StartBenchmarkRunRequest = {
				modelName,
				expectedProjectVersion,
				...(kvCacheType === null ? {} : { kvCacheType }),
				repeatMode,
				...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
			};
			const { data } = await callWithResponseValidation(startBenchmarkRun({ path: { projectId }, body, throwOnError: true }));
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

/** One matrix cell the node refused, in the same `code` vocabulary a single-run failure uses. */
export interface BenchmarkBatchRejection {
	modelName: string;
	kvCacheType: string | null;
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
			const { data } = await callWithResponseValidation(
				startBenchmarkRunBatch({
					path: { projectId },
					body: {
						expectedProjectVersion,
						items,
						repeatCount,
						warmup,
						repeatMode,
						...(repeatMode === "AnswerVariance" && answerVarianceTemperature !== null ? { answerVarianceTemperature } : {}),
					},
					throwOnError: true,
				}),
			);
			return {
				projectId,
				startedRunIds: (data.started ?? []).flatMap((item) => item.runIds ?? []),
				rejected: (data.rejected ?? []).map((item) => ({
					modelName: item.modelName,
					kvCacheType: item.kvCacheType ?? null,
					code: item.code,
					message: item.message,
				})),
			};
		},
		onSuccess: (result) => invalidate(result.projectId, ...result.startedRunIds),
	});
}

export function useCancelBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, target }: { run: BenchmarkRunRef; target: "Primary" | "Judge" }) => {
			const { data } = await callWithResponseValidation(
				cancelBenchmarkRun({ path: { runId: run.id }, body: { target, expectedVersion: run.version }, throwOnError: true }),
			);
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

export function useScoreBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, score }: { run: BenchmarkRunRef; score: number }) => {
			const { data } = await callWithResponseValidation(
				scoreBenchmarkRun({ path: { runId: run.id }, body: { score, expectedVersion: run.version }, throwOnError: true }),
			);
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

/** Drops the operator override so the run falls back to its judge score (or to unscored). */
export function useClearBenchmarkRunScore() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			const { data } = await callWithResponseValidation(
				clearBenchmarkRunScore({ path: { runId: run.id }, body: { expectedVersion: run.version }, throwOnError: true }),
			);
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

export function useRejudgeBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, force }: { run: BenchmarkRunRef; force: boolean }) => {
			const { data } = await callWithResponseValidation(
				rejudgeBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version, force }, throwOnError: true }),
			);
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
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
			await callWithResponseValidation(startBenchmarkRunFidelity({ path: { runId: run.id }, throwOnError: true }));
		},
		onSuccess: (_, run) => invalidate(run.projectId, run.id),
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
			await callWithResponseValidation(clearBenchmarkFidelityCache({ path: { projectId }, throwOnError: true }));
		},
		onSuccess: (_, projectId) => invalidate(projectId),
	});
}

export function useDeleteBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunRef) => {
			await callWithResponseValidation(
				deleteBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version }, throwOnError: true }),
			);
		},
		onSuccess: (_, run) => invalidate(run.projectId, run.id),
	});
}

// --- Task items -----------------------------------------------------------------------------------------------
// A project holds 1..N of them. Every mutation below moves the project's item-set hash, which resets the ranked
// cohort and unranks the cells that answered the old set — so all of them invalidate the runs and the cells too, and
// the caller is expected to say so BEFORE the operator clicks.

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
		queryKey: benchmarkQueryKeys.taskItems(projectId ?? ""),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listBenchmarkTaskItems({ path: { projectId: projectId as string }, signal, throwOnError: true }),
			);
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
	...(pruneVerifierOverrides(draft.verifierConfig) === null ? {} : { verifierConfig: pruneVerifierOverrides(draft.verifierConfig) }),
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
			const { data } = await callWithResponseValidation(
				createBenchmarkTaskItem({
					path: { projectId },
					body: { ...taskItemMutationBody(draft), expectedProjectVersion },
					throwOnError: true,
				}),
			);
			return toBenchmarkTaskItem(data);
		},
		onSuccess: (_item, variables) => invalidate(variables.projectId),
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
			const { data } = await callWithResponseValidation(
				updateBenchmarkTaskItem({
					path: { projectId, itemId: item.id },
					body: { ...taskItemMutationBody(draft), expectedVersion: item.version },
					throwOnError: true,
				}),
			);
			return toBenchmarkTaskItem(data);
		},
		onSuccess: (_item, variables) => invalidate(variables.projectId),
	});
}

export function useDeleteBenchmarkTaskItem() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: ({ projectId, item }: { projectId: string; item: Pick<BenchmarkTaskItem, "id" | "version"> }) =>
			callWithResponseValidation(
				deleteBenchmarkTaskItem({
					path: { projectId, itemId: item.id },
					body: { expectedVersion: item.version },
					throwOnError: true,
				}),
			),
		onSuccess: (_result, variables) => invalidate(variables.projectId),
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
			const { data } = await callWithResponseValidation(
				reorderBenchmarkTaskItems({ path: { projectId }, body: { itemIds: [...itemIds] }, throwOnError: true }),
			);
			return (data.items ?? []).map(toBenchmarkTaskItem).sort((left, right) => left.index - right.index);
		},
		onSuccess: (_items, variables) => invalidate(variables.projectId),
	});
}

// --- Cells ----------------------------------------------------------------------------------------------------

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
		queryKey: benchmarkQueryKeys.cells(projectId ?? ""),
		enabled: enabled && Boolean(projectId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listBenchmarkCells({ path: { projectId: projectId as string }, signal, throwOnError: true }),
			);
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
		queryKey: benchmarkQueryKeys.cellComparison(projectId ?? "", cellKeys),
		enabled: Boolean(projectId) && cellKeys.length >= 2,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				compareBenchmarkCells({
					path: { projectId: projectId as string },
					query: { cellKeys: [...cellKeys] },
					signal,
					throwOnError: true,
				}),
			);
			return {
				cells: (data.cells ?? []).map(toBenchmarkCell),
				cohort: toBenchmarkRankCohort(data.rankCohort),
				scorableItemCount: data.scorableItemCount ?? 0,
				pairedDeltas: (data.pairedDeltas ?? []).map(toBenchmarkPairedDelta),
			};
		},
	});
}
