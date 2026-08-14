import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import type { BenchmarkProjectDraft, BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";

const url = (path: string): string => buildLocalApiUrl(`benchmarks/${path}`);
const request = <T>(config: AxiosRequestConfig): Promise<T> => axiosInstance.request<T>(config).then((response) => response.data);

export const benchmarkApi = {
	listProjects: (signal?: AbortSignal) => request<unknown>({ method: "GET", url: url("projects"), signal }),
	getProject: (projectId: string, signal?: AbortSignal) =>
		request<unknown>({ method: "GET", url: url(`projects/${projectId}`), signal }),
	createProject: (draft: BenchmarkProjectDraft) => request<unknown>({ method: "POST", url: url("projects"), data: draft }),
	updateProject: (projectId: string, expectedVersion: number, draft: BenchmarkProjectDraft) =>
		request<unknown>({ method: "PUT", url: url(`projects/${projectId}`), data: { ...draft, projectId, expectedVersion } }),
	listRuns: (projectId: string, signal?: AbortSignal) =>
		request<unknown>({ method: "GET", url: url(`projects/${projectId}/runs`), params: { page: 1, pageSize: 100 }, signal }),
	getRun: (runId: string, signal?: AbortSignal) => request<unknown>({ method: "GET", url: url(`runs/${runId}`), signal }),
	startRun: (projectId: string, modelName: string, expectedProjectVersion: number) =>
		request<unknown>({
			method: "POST",
			url: url(`projects/${projectId}/runs`),
			data: { projectId, modelName, expectedProjectVersion },
		}),
	cancelRun: (run: BenchmarkRunDetail, target: "Primary" | "Judge") =>
		request<unknown>({
			method: "POST",
			url: url(`runs/${run.id}/cancel`),
			data: { runId: run.id, target, expectedVersion: run.version },
		}),
	scoreRun: (run: BenchmarkRunDetail, score: number) =>
		request<unknown>({
			method: "PUT",
			url: url(`runs/${run.id}/score`),
			data: { runId: run.id, score, expectedVersion: run.version },
		}),
	deleteRun: (run: BenchmarkRunDetail) =>
		request<void>({ method: "DELETE", url: url(`runs/${run.id}`), params: { expectedVersion: run.version } }),
	listEligibleModels: (contextTokens?: number, signal?: AbortSignal) =>
		request<unknown>({
			method: "GET",
			url: url("eligible-models"),
			params: contextTokens ? { contextTokens } : undefined,
			signal,
		}),
};
