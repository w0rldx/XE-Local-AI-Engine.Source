import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelBenchmarkRun,
	createBenchmarkProject,
	deleteBenchmarkRun,
	getBenchmarkProject,
	getBenchmarkRun,
	listBenchmarkProjects,
	listBenchmarkRuns,
	listEligibleBenchmarkModels,
	scoreBenchmarkRun,
	startBenchmarkRun,
	updateBenchmarkProject,
} from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import type { StartBenchmarkRunBody } from "@/features/benchmarks/models/BenchmarkLaunchEvidenceWire";
import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRunDetail,
	toBenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkEligibleModel,
	BenchmarkKvCacheType,
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkProjectSummary,
	BenchmarkRunDetail,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";

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
};

const activeRunPollIntervalMs = 2_000;

function hasActiveRun(runs: BenchmarkRunSummary[] | undefined): boolean {
	return (
		runs?.some(
			(run) =>
				["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
				["Pending", "Queued", "Running"].includes(run.judgeStatus),
		) ?? false
	);
}

function isRunActive(run: BenchmarkRunDetail | undefined): boolean {
	return Boolean(
		run &&
			(["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
				["Pending", "Queued", "Running"].includes(run.judgeStatus)),
	);
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

export function useBenchmarkRuns(projectId: string | null) {
	return useQuery<BenchmarkRunSummary[]>({
		queryKey: benchmarkQueryKeys.runs(projectId ?? ""),
		enabled: Boolean(projectId),
		refetchInterval: (query) => (hasActiveRun(query.state.data) ? activeRunPollIntervalMs : false),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listBenchmarkRuns({
					path: { projectId: projectId as string },
					query: { page: 1, pageSize: 100 },
					signal,
					throwOnError: true,
				}),
			);
			return (data.items ?? []).map(toBenchmarkRunSummary);
		},
	});
}

export function useBenchmarkRun(runId: string) {
	return useQuery<BenchmarkRunDetail>({
		queryKey: benchmarkQueryKeys.run(runId),
		refetchInterval: (query) => (isRunActive(query.state.data) ? activeRunPollIntervalMs : false),
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

function useBenchmarkInvalidation() {
	const queryClient = useQueryClient();
	return async (projectId?: string, runId?: string): Promise<void> => {
		await queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.projects });
		if (projectId) {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.project(projectId) }),
				queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.runs(projectId) }),
			]);
		}
		if (runId) {
			await queryClient.invalidateQueries({ queryKey: benchmarkQueryKeys.run(runId) });
		}
	};
}

export function useCreateBenchmarkProject() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (draft: BenchmarkProjectDraft) => {
			const { data } = await callWithResponseValidation(createBenchmarkProject({ body: draft, throwOnError: true }));
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
				updateBenchmarkProject({ path: { projectId }, body: { ...draft, expectedVersion }, throwOnError: true }),
			);
			return toBenchmarkProjectDetail(data);
		},
		onSuccess: (project) => invalidate(project.id),
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
		}: {
			projectId: string;
			modelName: string;
			expectedProjectVersion: number;
			/** null = Auto: the member is omitted so the node applies its own rule at freeze. */
			kvCacheType: BenchmarkKvCacheType | null;
		}) => {
			// Swap seam: `StartBenchmarkRunBody` is the hand-written mirror; after the regen this is the generated body type.
			const body: StartBenchmarkRunBody = {
				modelName,
				expectedProjectVersion,
				...(kvCacheType === null ? {} : { kvCacheType }),
			};
			const { data } = await callWithResponseValidation(startBenchmarkRun({ path: { projectId }, body, throwOnError: true }));
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

export function useCancelBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, target }: { run: BenchmarkRunDetail; target: "Primary" | "Judge" }) => {
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
		mutationFn: async ({ run, score }: { run: BenchmarkRunDetail; score: number }) => {
			const { data } = await callWithResponseValidation(
				scoreBenchmarkRun({ path: { runId: run.id }, body: { score, expectedVersion: run.version }, throwOnError: true }),
			);
			return toBenchmarkRunDetail(data);
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}

export function useDeleteBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async (run: BenchmarkRunDetail) => {
			await callWithResponseValidation(
				deleteBenchmarkRun({ path: { runId: run.id }, body: { expectedVersion: run.version }, throwOnError: true }),
			);
		},
		onSuccess: (_, run) => invalidate(run.projectId, run.id),
	});
}
