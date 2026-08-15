import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	deleteTrainingDataset,
	deleteTrainingDefinition,
	exportTrainingDataset,
	generateTrainingDataset,
	listToolMocks,
	listTrainingDatasets,
	listTrainingDefinitions,
	listTrainingSamples,
	reviewTrainingSample,
	verifyToolMock,
} from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import type { SampleLabel, SampleReviewState, ToolMock, TrainingDataset, TrainingDefinition, TrainingSample } from "@/features/training/models/TrainingModels";
import { toToolMock, toTrainingDataset, toTrainingDefinition, toTrainingSample } from "@/features/training/models/TrainingModels";

// Server state for the training dataset surface. Calls the generated hey-api SDK directly through the shared
// `callWithResponseValidation` bridge (the benchmarks pattern) so a hook can derive its poll cadence from the
// already-mapped domain data.

const trainingQueryKeys = {
	definitions: ["training", "definitions"] as const,
	datasets: ["training", "datasets"] as const,
	samples: (datasetId: string, page: number, reviewState?: SampleReviewState) =>
		["training", "datasets", datasetId, "samples", page, reviewState ?? "all"] as const,
	mocks: ["training", "mocks"] as const,
};

const generatingPollIntervalMs = 2_000;

export function useTrainingDefinitions() {
	return useQuery<TrainingDefinition[]>({
		queryKey: trainingQueryKeys.definitions,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(listTrainingDefinitions({ signal, throwOnError: true }));
			return (data.items ?? []).map(toTrainingDefinition);
		},
	});
}

export function useTrainingDatasets() {
	return useQuery<TrainingDataset[]>({
		queryKey: trainingQueryKeys.datasets,
		// A generating dataset is the only reason to poll; the hub carries the fine-grained progress.
		refetchInterval: (query) => (query.state.data?.some((dataset) => dataset.status === "Generating") ? generatingPollIntervalMs : false),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(listTrainingDatasets({ signal, throwOnError: true }));
			return (data.items ?? []).map(toTrainingDataset);
		},
	});
}

export function useTrainingSamples(datasetId: string | null, page: number, reviewState?: SampleReviewState) {
	return useQuery<{ items: TrainingSample[]; totalCount: number }>({
		queryKey: trainingQueryKeys.samples(datasetId ?? "", page, reviewState),
		enabled: Boolean(datasetId),
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(
				listTrainingSamples({
					path: { datasetId: datasetId as string },
					query: { page, pageSize: 20, reviewState },
					signal,
					throwOnError: true,
				}),
			);
			return { items: (data.items ?? []).map(toTrainingSample), totalCount: data.totalCount ?? 0 };
		},
	});
}

export function useToolMocks() {
	return useQuery<ToolMock[]>({
		queryKey: trainingQueryKeys.mocks,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(listToolMocks({ signal, throwOnError: true }));
			return (data.items ?? []).map(toToolMock);
		},
	});
}

export function useGenerateDataset() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { definitionId: string; expectedVersion: number; name: string }) => {
			const { data } = await callWithResponseValidation(
				generateTrainingDataset({
					path: { definitionId: input.definitionId },
					body: { expectedVersion: input.expectedVersion, name: input.name },
					throwOnError: true,
				}),
			);
			return toTrainingDataset(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.datasets });
		},
	});
}

export function useDeleteTrainingDefinition() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { definitionId: string; expectedVersion: number }) => {
			await deleteTrainingDefinition({
				path: { definitionId: input.definitionId },
				body: { expectedVersion: input.expectedVersion },
				throwOnError: true,
			});
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.definitions });
		},
	});
}

export function useDeleteTrainingDataset() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { datasetId: string; expectedVersion: number }) => {
			await deleteTrainingDataset({
				path: { datasetId: input.datasetId },
				body: { expectedVersion: input.expectedVersion },
				throwOnError: true,
			});
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.datasets });
		},
	});
}

/** Any review verb bumps the dataset revision and fingerprint server-side, so the dataset list is invalidated too. */
export function useReviewTrainingSample() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { datasetId: string; sampleId: string; verb: "Approve" | "Reject" | "Relabel"; label?: SampleLabel }) => {
			const { data } = await callWithResponseValidation(
				reviewTrainingSample({
					path: { datasetId: input.datasetId, sampleId: input.sampleId },
					body: { verb: input.verb, label: input.label },
					throwOnError: true,
				}),
			);
			return toTrainingSample(data);
		},
		onSuccess: async (_result, input) => {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: ["training", "datasets", input.datasetId, "samples"] }),
				queryClient.invalidateQueries({ queryKey: trainingQueryKeys.datasets }),
			]);
		},
	});
}

export function useVerifyToolMock() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { mockId: string; expectedVersion: number }) => {
			const { data } = await callWithResponseValidation(
				verifyToolMock({ path: { mockId: input.mockId }, body: { expectedVersion: input.expectedVersion }, throwOnError: true }),
			);
			return toToolMock(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.mocks });
		},
	});
}

export function useExportTrainingDataset() {
	return useMutation({
		mutationFn: async (input: { datasetId: string; format: "Jsonl" | "Hermes" }) => {
			const { data } = await callWithResponseValidation(
				exportTrainingDataset({ path: { datasetId: input.datasetId }, query: { format: input.format }, throwOnError: true }),
			);
			return { content: data.content ?? "", lineCount: data.lineCount ?? 0 };
		},
	});
}
