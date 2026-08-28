import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelTrainingDataset,
	createTrainingDefinition,
	deleteTrainingDataset,
	deleteTrainingDefinition,
	exportTrainingDataset,
	generateTrainingDataset,
	listToolMocks,
	listTrainingDatasets,
	listTrainingDefinitions,
	listTrainingSamples,
	reviewTrainingSample,
	updateTrainingDefinition,
	verifyToolMock,
} from "@/core/api/generated";
import type { XeLocalAiEngineClientServicesTrainingDatasetsDatasetDefinitionBodyV1 as DatasetDefinitionBody } from "@/core/api/generated";
import { getToolCapableModelsOptions, listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { callWithResponseValidation, withResponseValidation } from "@/core/api/ResponseValidation";
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

// External-provider model ids are namespaced `ext:{connection}/{wireId}`. Matching the prefix is how both layers
// recognize them, so the two agree without the frontend having to resolve a connection.
const EXTERNAL_MODEL_ID_PREFIX = "ext:";

/**
 * The models a definition may name as its teacher or critic: node-local only (invariant #5), narrowed to the
 * tool-capable ones because a tool-calling dataset is what the teacher has to produce. An empty capability list means
 * the node has not populated that capability yet — then every local model is offered rather than none, the same
 * "do not enforce" posture the agent form takes.
 *
 * External-provider models are excluded outright, whatever their declared trust: training generation and evaluation
 * are a GGUF-leased pipeline and the backend refuses `external` teachers and critics with a typed validation error.
 * A declared-LOCAL external model would otherwise slip past that intent here — the tool-capable allow-list carries
 * `ext:` ids (models declared tool-capable join it node-wide), so narrowing by capability alone would not drop them.
 * The same list feeds both the teacher and the critic picker (DefinitionEditorDialog), so one filter covers both.
 */
export function useTeacherModelNames(): string[] {
	const localModels = useQuery(withResponseValidation(listLocalModelsOptions()));
	const toolCapable = useQuery(withResponseValidation(getToolCapableModelsOptions()));

	const localNames = (localModels.data?.items ?? [])
		.map((model) => model.modelName ?? "")
		.filter((name) => name.length > 0 && !name.startsWith(EXTERNAL_MODEL_ID_PREFIX));
	const capableNames = new Set((toolCapable.data?.models ?? []).filter((name) => !name.startsWith(EXTERNAL_MODEL_ID_PREFIX)));
	if (capableNames.size === 0) {
		return localNames;
	}
	const narrowed = localNames.filter((name) => capableNames.has(name));
	return narrowed.length > 0 ? narrowed : localNames;
}

export function useCreateTrainingDefinition() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { name: string; body: DatasetDefinitionBody }) => {
			const { data } = await callWithResponseValidation(
				createTrainingDefinition({ body: { name: input.name, body: input.body }, throwOnError: true }),
			);
			return toTrainingDefinition(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.definitions });
		},
	});
}

export function useUpdateTrainingDefinition() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { definitionId: string; expectedVersion: number; name: string; body: DatasetDefinitionBody }) => {
			const { data } = await callWithResponseValidation(
				updateTrainingDefinition({
					path: { definitionId: input.definitionId },
					body: { expectedVersion: input.expectedVersion, name: input.name, body: input.body },
					throwOnError: true,
				}),
			);
			return toTrainingDefinition(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: trainingQueryKeys.definitions });
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

/** Asks the node to stop an in-flight generation. The dataset itself survives, with whatever samples it already has. */
export function useCancelTrainingDataset() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { datasetId: string }) => {
			await cancelTrainingDataset({ path: { datasetId: input.datasetId }, throwOnError: true });
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
