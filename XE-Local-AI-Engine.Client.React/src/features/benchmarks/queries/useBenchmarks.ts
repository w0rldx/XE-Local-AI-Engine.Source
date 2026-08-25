import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelBenchmarkRun,
	clearBenchmarkRunScore,
	createBenchmarkProject,
	deleteBenchmarkRun,
	getBenchmarkProject,
	getBenchmarkRubricPresets,
	getBenchmarkRun,
	listBenchmarkProjects,
	listBenchmarkRuns,
	listEligibleBenchmarkModels,
	rejudgeBenchmarkProject,
	rejudgeBenchmarkRun,
	scoreBenchmarkRun,
	startBenchmarkRun,
	startBenchmarkRunBatch,
	updateBenchmarkJudgePolicy,
	updateBenchmarkProject,
} from "@/core/api/generated";
import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunBatchItem as StartBenchmarkRunBatchItem,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunRequest as StartBenchmarkRunRequest,
} from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRankCohort,
	toBenchmarkRubric,
	toBenchmarkRunDetail,
	toBenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkEligibleModel,
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

// Server state for the benchmarks surface. Calls the generated hey-api SDK fns DIRECTLY through the shared
// `callWithResponseValidation` bridge (the same imperative pattern as useLoadedModels / the chat adapter) rather than
// the generated TanStack `*Options()` wrappers, because several hooks below derive their poll cadence from the
// already-mapped domain data (`query.state.data`), which `select`-based options would not expose pre-mapping.

const benchmarkQueryKeys = {
	projects: ["benchmarks", "projects"] as const,
	project: (id: string) => ["benchmarks", "projects", id] as const,
	runs: (projectId: string) => ["benchmarks", "projects", projectId, "runs"] as const,
	run: (id: string) => ["benchmarks", "runs", id] as const,
	models: (contextTokens?: number) => ["benchmarks", "eligible-models", contextTokens] as const,
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

/** The three rubrics the node offers as starting points. */
export interface BenchmarkRubricPresets {
	default: BenchmarkRubric | null;
	programming: BenchmarkRubric | null;
	reasoning: BenchmarkRubric | null;
}

const isRunActive = (run: Pick<BenchmarkRunSummary, "primaryStatus" | "judge">): boolean =>
	["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) || isJudgeActive(run.judge.state);

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
		// Flattened here so every consumer keeps seeing one ranked list and never the page machinery.
		select: (data) => ({
			items: data.pages.flatMap((page) => page.items),
			cohort: (data.pages[0] as BenchmarkRunList).cohort,
			totalCount: (data.pages[0] as BenchmarkRunList).totalCount,
		}),
	});
	// The promise is the caller's to ignore: a failed page read is already reported as the query's error state.
	return { ...query, loadMore: query.fetchNextPage };
}

export function useBenchmarkRun(runId: string) {
	return useQuery<BenchmarkRunDetail>({
		queryKey: benchmarkQueryKeys.run(runId),
		refetchInterval: (query) => (query.state.data && isRunActive(query.state.data) ? activeRunPollIntervalMs : false),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getBenchmarkRun({ path: { runId }, signal, throwOnError: true }));
			return toBenchmarkRunDetail(data);
		},
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
	judgeModelName: draft.judgeModelName,
	judgeContextTokens: draft.judgeContextTokens,
	...(draft.rubric === null ? {} : { rubric: draft.rubric }),
	...(draft.referenceAnswer === null ? {} : { referenceAnswer: draft.referenceAnswer }),
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
				updateBenchmarkJudgePolicy({ path: { projectId }, body: { policy, expectedVersion, confirmRejudge }, throwOnError: true }),
			);
			const enqueuedRunIds = data.enqueuedRunIds ?? [];
			return { project: toBenchmarkProjectDetail(data.project), enqueuedRunIds, enqueuedRunCount: enqueuedRunIds.length };
		},
		onSuccess: (change) => invalidate(change.project.id, ...change.enqueuedRunIds),
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
