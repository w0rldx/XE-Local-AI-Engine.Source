import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	toBenchmarkEligibleModel,
	toBenchmarkProjectDetail,
	toBenchmarkProjectSummary,
	toBenchmarkRunDetail,
	toBenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkMappers";
import type {
	BenchmarkEligibleModel,
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkProjectSummary,
	BenchmarkRunDetail,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkApi } from "@/features/benchmarks/queries/BenchmarkApi";

const benchmarkQueryKeys = {
	projects: ["benchmarks", "projects"] as const,
	project: (id: string) => ["benchmarks", "projects", id] as const,
	runs: (projectId: string) => ["benchmarks", "projects", projectId, "runs"] as const,
	run: (id: string) => ["benchmarks", "runs", id] as const,
	models: (contextTokens?: number) => ["benchmarks", "eligible-models", contextTokens] as const,
};

const record = (value: unknown): Record<string, unknown> =>
	value && typeof value === "object" ? (value as Record<string, unknown>) : {};
const items = (value: unknown): Record<string, unknown>[] => {
	const candidate = record(value)["items"];
	return Array.isArray(candidate) ? candidate.map(record) : [];
};

export function useBenchmarkProjects() {
	return useQuery<BenchmarkProjectSummary[]>({
		queryKey: benchmarkQueryKeys.projects,
		queryFn: async ({ signal }) => {
			const data = await benchmarkApi.listProjects(signal);
			return items(data).map(toBenchmarkProjectSummary);
		},
	});
}

export function useBenchmarkProject(projectId: string | null) {
	return useQuery<BenchmarkProjectDetail>({
		queryKey: benchmarkQueryKeys.project(projectId ?? ""),
		enabled: Boolean(projectId),
		queryFn: async ({ signal }) => {
			const data = await benchmarkApi.getProject(projectId as string, signal);
			return toBenchmarkProjectDetail(record(data));
		},
	});
}

export function useBenchmarkRuns(projectId: string | null) {
	return useQuery<BenchmarkRunSummary[]>({
		queryKey: benchmarkQueryKeys.runs(projectId ?? ""),
		enabled: Boolean(projectId),
		refetchInterval: (query) => {
			const data = query.state.data;
			return data?.some(
				(run) =>
					["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
					["Pending", "Queued", "Running"].includes(run.judgeStatus),
			)
				? 2_000
				: false;
		},
		queryFn: async ({ signal }) => {
			const data = await benchmarkApi.listRuns(projectId as string, signal);
			return items(data).map(toBenchmarkRunSummary);
		},
	});
}

export function useBenchmarkRun(runId: string) {
	return useQuery<BenchmarkRunDetail>({
		queryKey: benchmarkQueryKeys.run(runId),
		refetchInterval: (query) => {
			const run = query.state.data;
			return run &&
				(["Queued", "Running", "CancelRequested"].includes(run.primaryStatus) ||
					["Pending", "Queued", "Running"].includes(run.judgeStatus))
				? 2_000
				: false;
		},
		queryFn: async ({ signal }) => {
			const data = await benchmarkApi.getRun(runId, signal);
			return toBenchmarkRunDetail(record(data));
		},
	});
}

export function useEligibleBenchmarkModels(contextTokens?: number) {
	return useQuery<BenchmarkEligibleModel[]>({
		queryKey: benchmarkQueryKeys.models(contextTokens),
		queryFn: async ({ signal }) => {
			const data = await benchmarkApi.listEligibleModels(contextTokens, signal);
			return items(data).map(toBenchmarkEligibleModel);
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
			const data = await benchmarkApi.createProject(draft);
			return toBenchmarkProjectDetail(record(data));
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
			const data = await benchmarkApi.updateProject(projectId, expectedVersion, draft);
			return toBenchmarkProjectDetail(record(data));
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
		}: {
			projectId: string;
			modelName: string;
			expectedProjectVersion: number;
		}) => {
			const data = await benchmarkApi.startRun(projectId, modelName, expectedProjectVersion);
			return toBenchmarkRunDetail(record(data));
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}
export function useCancelBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, target }: { run: BenchmarkRunDetail; target: "Primary" | "Judge" }) => {
			const data = await benchmarkApi.cancelRun(run, target);
			return toBenchmarkRunDetail(record(data));
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}
export function useScoreBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: async ({ run, score }: { run: BenchmarkRunDetail; score: number }) => {
			const data = await benchmarkApi.scoreRun(run, score);
			return toBenchmarkRunDetail(record(data));
		},
		onSuccess: (run) => invalidate(run.projectId, run.id),
	});
}
export function useDeleteBenchmarkRun() {
	const invalidate = useBenchmarkInvalidation();
	return useMutation({
		mutationFn: (run: BenchmarkRunDetail) => benchmarkApi.deleteRun(run),
		onSuccess: (_, run) => invalidate(run.projectId, run.id),
	});
}
