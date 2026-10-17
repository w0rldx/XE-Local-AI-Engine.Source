import { type QueryClient, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientServicesTrainingDatasetsDatasetDefinitionBodyV1 as DatasetDefinitionBody } from "@/core/api/generated";
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
import {
	getToolCapableModelsOptions,
	listLocalModelsOptions,
	listToolMocksQueryKey,
	listTrainingDatasetsQueryKey,
	listTrainingDefinitionsQueryKey,
	listTrainingSamplesQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type {
	SampleLabel,
	SampleReviewState,
	ToolMock,
	TrainingDataset,
	TrainingDefinition,
	TrainingSample,
} from "@/features/training/models/TrainingModels";
import { toToolMock, toTrainingDataset, toTrainingDefinition, toTrainingSample } from "@/features/training/models/TrainingModels";

// Server state for the training dataset surface. Calls the generated hey-api SDK directly (the benchmarks
// pattern) so a hook can derive its poll cadence from the
// already-mapped domain data. Cache keys are the generated `*QueryKey` ones.

const generatingPollIntervalMs = 2_000;

export function useTrainingDefinitions() {
	return useQuery<TrainingDefinition[]>({
		queryKey: listTrainingDefinitionsQueryKey(),
		queryFn: async ({ signal }) => {
			const { data } = await listTrainingDefinitions({ signal });
			return (data.items ?? []).map(toTrainingDefinition);
		},
	});
}

export function useTrainingDatasets() {
	return useQuery<TrainingDataset[]>({
		queryKey: listTrainingDatasetsQueryKey(),
		// A generating dataset is the only reason to poll; the hub carries the fine-grained progress.
		refetchInterval: (query) =>
			query.state.data?.some((dataset) => dataset.status === "Generating") ? generatingPollIntervalMs : false,
		queryFn: async ({ signal }) => {
			const { data } = await listTrainingDatasets({ signal });
			return (data.items ?? []).map(toTrainingDataset);
		},
	});
}

export function useTrainingSamples(datasetId: string | null, page: number, reviewState?: SampleReviewState) {
	const request = { path: { datasetId: datasetId ?? "" }, query: { page, pageSize: 20, reviewState } };
	return useQuery<{ items: TrainingSample[]; totalCount: number }>({
		queryKey: listTrainingSamplesQueryKey(request),
		enabled: Boolean(datasetId),
		queryFn: async ({ signal }) => {
			const { data } = await listTrainingSamples({ ...request, signal });
			return { items: (data.items ?? []).map(toTrainingSample), totalCount: data.totalCount ?? 0 };
		},
	});
}

export function useToolMocks() {
	return useQuery<ToolMock[]>({
		queryKey: listToolMocksQueryKey(),
		queryFn: async ({ signal }) => {
			const { data } = await listToolMocks({ signal });
			return (data.items ?? []).map(toToolMock);
		},
	});
}

/**
 * The dataset list plus every dataset's sample pages (all pages and filters): the old `["training", "datasets"]` prefix
 * scope. The sample review does not poll, so a cancel or generate that changes a dataset's samples must reach it here.
 */
function invalidateDatasets(queryClient: QueryClient): Promise<unknown> {
	return Promise.all([
		queryClient.invalidateQueries({ queryKey: listTrainingDatasetsQueryKey() }),
		queryClient.invalidateQueries({ queryKey: [{ _id: "listTrainingSamples" }] }),
	]);
}

export function useGenerateDataset() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { definitionId: string; expectedVersion: number; name: string }) => {
			const { data } = await generateTrainingDataset({
				path: { definitionId: input.definitionId },
				body: { expectedVersion: input.expectedVersion, name: input.name },
			});
			return toTrainingDataset(data);
		},
		onSuccess: async () => {
			await invalidateDatasets(queryClient);
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
	const localModels = useQuery(listLocalModelsOptions());
	const toolCapable = useQuery(getToolCapableModelsOptions());

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
			const { data } = await createTrainingDefinition({ body: { name: input.name, body: input.body } });
			return toTrainingDefinition(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: listTrainingDefinitionsQueryKey() });
		},
	});
}

export function useUpdateTrainingDefinition() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { definitionId: string; expectedVersion: number; name: string; body: DatasetDefinitionBody }) => {
			const { data } = await updateTrainingDefinition({
				path: { definitionId: input.definitionId },
				body: { expectedVersion: input.expectedVersion, name: input.name, body: input.body },
			});
			return toTrainingDefinition(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: listTrainingDefinitionsQueryKey() });
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
			});
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: listTrainingDefinitionsQueryKey() });
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
			});
		},
		onSuccess: async () => {
			await invalidateDatasets(queryClient);
		},
	});
}

/** Asks the node to stop an in-flight generation. The dataset itself survives, with whatever samples it already has. */
export function useCancelTrainingDataset() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { datasetId: string }) => {
			await cancelTrainingDataset({ path: { datasetId: input.datasetId } });
		},
		onSuccess: async () => {
			await invalidateDatasets(queryClient);
		},
	});
}

/** Any review verb bumps the dataset revision and fingerprint server-side, so the dataset list is invalidated too. */
export function useReviewTrainingSample() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: {
			datasetId: string;
			sampleId: string;
			verb: "Approve" | "Reject" | "Relabel";
			label?: SampleLabel;
		}) => {
			const { data } = await reviewTrainingSample({
				path: { datasetId: input.datasetId, sampleId: input.sampleId },
				body: { verb: input.verb, label: input.label },
			});
			return toTrainingSample(data);
		},
		onSuccess: async () => {
			await invalidateDatasets(queryClient);
		},
	});
}

export function useVerifyToolMock() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (input: { mockId: string; expectedVersion: number }) => {
			const { data } = await verifyToolMock({ path: { mockId: input.mockId }, body: { expectedVersion: input.expectedVersion } });
			return toToolMock(data);
		},
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: listToolMocksQueryKey() });
		},
	});
}

export function useExportTrainingDataset() {
	return useMutation({
		mutationFn: async (input: { datasetId: string; format: "Jsonl" | "Hermes" }) => {
			const { data } = await exportTrainingDataset({ path: { datasetId: input.datasetId }, query: { format: input.format } });
			return { content: data.content ?? "", lineCount: data.lineCount ?? 0 };
		},
	});
}
