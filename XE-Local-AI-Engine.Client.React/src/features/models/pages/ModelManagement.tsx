import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconRefresh, IconRobot } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import {
	deleteLocalModelMutation,
	deleteModelKindMutation,
	getLocalModelDetailsOptions,
	getLocalModelDetailsQueryKey,
	listLocalModelsOptions,
	listLocalModelsQueryKey,
	putModelKindMutation,
	selectLocalModelMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { GgufBrowsePanel } from "@/features/models/components/GgufBrowsePanel";
import { GgufDownloadDialog } from "@/features/models/components/GgufDownloadDialog";
import { InstalledModelsTable } from "@/features/models/components/InstalledModelsTable";
import { ModelDetailsDialog } from "@/features/models/components/ModelDetailsDialog";
import { defaultGgufQuant, type GgufRepository, type GgufRepositoryFile } from "@/features/models/models/GgufModels";
import { toLocalModelViewModel } from "@/features/models/models/LocalModelMappers";
import {
	useActiveGgufDownloads,
	useBrowseGgufRepositories,
	useCancelGgufDownload,
	useStartGgufDownload,
} from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates */

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected local model error";
}

function ggufErrorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

export function ModelManagement() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	// The model whose details dialog is open (also the only model whose details endpoint is fetched).
	const [detailsModelName, setDetailsModelName] = useState<string | undefined>();
	const [detailsModalOpened, { open: openDetailsModal, close: closeDetailsModal }] = useDisclosure(false);

	// GGUF browse + download flow (relocated from the model-fit advisor — it is a model-acquisition action). The
	// committed browse term + the in-flight download set live in a shared store so they survive a remount AND so a
	// download handed off from the advisor's recommendation row becomes visible + cancellable here. The open-quant-
	// picker repo is page-local. useActiveGgufDownloads polls the backend for byte-level progress and reconciles
	// the store so downloads survive navigation/refresh — the backend list is the authoritative source of truth.
	const browseQuery = useGgufBrowseStore((state) => state.browseQuery);
	const setBrowseQuery = useGgufBrowseStore((state) => state.actions.setBrowseQuery);
	const inFlightDownloads = useGgufBrowseStore((state) => state.inFlightDownloads);
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	// Polls GET /downloads every second while any download is Running. Reconciles the store so entries started before
	// a page refresh rehydrate, and terminal entries (Completed/Cancelled/Failed) are removed from the store.
	const downloadStatuses = useActiveGgufDownloads();
	// The repo whose quant picker dialog is open (null = closed). Selecting a browse row opens the dialog so the
	// operator picks the exact quant (incl. Unsloth Dynamic UD- quants) instead of always pulling the default Q4_K_M.
	const [downloadRepo, setDownloadRepo] = useState<GgufRepository | null>(null);

	const browseQueryResult = useBrowseGgufRepositories(browseQuery, true);
	const startGgufDownloadMutation = useStartGgufDownload();
	const cancelGgufDownloadMutation = useCancelGgufDownload();

	// Reads run through the generated hey-api `*Options()` (which wire the shared axios instance + TanStack Query
	// AbortSignal automatically), wrapped in withResponseValidation so a zod response-shape failure surfaces as an
	// ApiError. The list query keeps the generated response envelope (isAvailable / selectedModelName / error) and
	// maps its optional-field items to the strict view-models in a memo. Invalidation uses the generated query-key
	// factories so every cached variant of an endpoint refetches.
	const modelsQuery = useQuery(withResponseValidation(listLocalModelsOptions()));
	const modelsResponse = modelsQuery.data;
	const modelItems = useMemo(() => modelsResponse?.items ?? [], [modelsResponse]);
	const modelViewModels = useMemo(() => modelItems.map(toLocalModelViewModel), [modelItems]);

	// Details are fetched only while a model's dialog is open — there is no longer a persistent details card.
	const detailsQuery = useQuery({
		...withResponseValidation(getLocalModelDetailsOptions({ path: { modelName: detailsModelName ?? "" } })),
		enabled: Boolean(detailsModalOpened && detailsModelName && modelsResponse?.isAvailable),
	});

	const invalidateList = useCallback(() => queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() }), [queryClient]);
	const invalidateListAndDetails = useCallback(
		() =>
			Promise.all([
				invalidateList(),
				queryClient.invalidateQueries({
					queryKey: getLocalModelDetailsQueryKey({ path: { modelName: detailsModelName ?? "" } }),
				}),
			]).then(() => undefined),
		[invalidateList, queryClient, detailsModelName],
	);

	const selectMutation = useMutation({
		...withResponseValidation(selectLocalModelMutation()),
		onSuccess: async (selection) => {
			toast.success(`Default local model set to ${selection.selectedModelName ?? ""}.`);
			await invalidateListAndDetails();
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const deleteMutation = useMutation({
		...withResponseValidation(deleteLocalModelMutation()),
		onSuccess: async (response) => {
			toast.success(`Model ${response.modelName ?? ""} deleted.`);
			closeDetailsModal();
			setDetailsModelName(undefined);
			await invalidateListAndDetails();
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const setKindMutation = useMutation({
		...withResponseValidation(putModelKindMutation()),
		// Setting an override does NOT probe Ollama, so the response's detectedKind may still be Unknown. Invalidate
		// the list so the next refetch runs lazy detection and the row reflects the freshly detected kind, not the
		// override response.
		onSuccess: async () => {
			await invalidateList();
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	const resetKindMutation = useMutation({
		...withResponseValidation(deleteModelKindMutation()),
		onSuccess: async () => {
			await invalidateList();
		},
		onError: (error) => toast.error(errorMessage(error)),
	});

	// Action errors surface via each mutation's onError toast above (no inline banner).
	const isActionPending =
		selectMutation.isPending || deleteMutation.isPending || setKindMutation.isPending || resetKindMutation.isPending;
	const detailsModel = modelViewModels.find((model) => model.modelName === detailsModelName);

	const openDetails = useCallback(
		(modelName: string) => {
			setDetailsModelName(modelName);
			openDetailsModal();
		},
		[openDetailsModal],
	);

	const confirmDelete = useCallback(
		async (modelName: string) => {
			const confirmed = await confirm({
				title: "Delete model",
				description: `Delete '${modelName}' from the local model store? This cannot be undone.`,
				confirmationText: "Delete",
				cancellationText: "Cancel",
			});

			if (confirmed) {
				deleteMutation.mutate({ path: { modelName } });
			}
		},
		[confirm, deleteMutation],
	);

	// Starts a GGUF download by repo id (the model name the backend resolves rides the response). On success the model
	// is tracked as in-flight (in the shared store); alreadyInFlight responses are surfaced too (already running).
	const startGgufDownload = useCallback(
		(repoId: string, fileName?: string, quant?: string): void => {
			startGgufDownloadMutation.mutate(
				{ repoId, fileName, quant },
				{
					onSuccess: (response) => {
						const modelName = response?.modelName ?? repoId;
						markInFlight(modelName);
						if (response?.alreadyInFlight) {
							toast.info(t("pages.models.gguf.download.alreadyInFlight", "That download is already in progress."));
						} else {
							toast.success(t("pages.models.gguf.download.started", "Download started."));
						}
					},
					onError: (error) =>
						toast.error(ggufErrorMessage(error, t("pages.models.gguf.download.error", "Could not start the download."))),
				},
			);
		},
		[startGgufDownloadMutation, markInFlight, t],
	);

	// Opens the quant picker for a browse row instead of immediately pulling the default quant.
	const handleBrowseDownload = (repository: GgufRepository): void => {
		setDownloadRepo(repository);
	};

	// Confirms a specific quant from the picker: downloads the exact chosen file (fileName is resolved verbatim by the
	// backend, so a Dynamic UD- quant downloads unambiguously) and closes the dialog.
	const handleConfirmQuantDownload = (repoId: string, file: GgufRepositoryFile): void => {
		startGgufDownload(repoId, file.fileName, file.quant);
		setDownloadRepo(null);
	};

	// Fallback used when the picker has no files to offer (degraded/unreachable inspection): download the default quant
	// by repo id only, restoring the pre-picker one-click capability so a degraded inspect never blocks downloading.
	const handleConfirmDefaultDownload = (repoId: string): void => {
		startGgufDownload(repoId, undefined, defaultGgufQuant);
		setDownloadRepo(null);
	};

	const handleCancelDownload = (modelName: string): void => {
		cancelGgufDownloadMutation.mutate(modelName, {
			onSuccess: () => {
				removeInFlight(modelName);
				toast.success(t("pages.models.gguf.download.cancelled", "Download cancelled."));
			},
			onError: (error) =>
				toast.error(ggufErrorMessage(error, t("pages.models.gguf.download.cancelError", "Could not cancel the download."))),
		});
	};

	const downloadingRepoId = startGgufDownloadMutation.isPending ? (startGgufDownloadMutation.variables?.repoId ?? null) : null;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							Worker Node
						</Text>
						<Title order={2}>Model management</Title>
						<Text c="dimmed">List, select, and delete installed local models.</Text>
					</Stack>
					<Group gap="sm">
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => modelsQuery.refetch()}
							disabled={modelsQuery.isFetching}
						>
							Refresh
						</Button>
					</Group>
				</Group>

				{modelsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">Loading local models…</Text>
					</Group>
				) : null}

				{modelsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{errorMessage(modelsQuery.error)}
					</Alert>
				) : null}

				<Card withBorder={true} radius="md" p="lg">
					<Stack gap="md">
						<Group justify="space-between">
							<Title order={3}>Installed models</Title>
							<IconRobot size={22} />
						</Group>
						<InstalledModelsTable
							models={modelViewModels}
							isActionPending={isActionPending}
							onOpenDetails={openDetails}
							onSetDefault={(modelName) => selectMutation.mutate({ body: { modelName } })}
							onDelete={confirmDelete}
							onResetKind={(modelName) => resetKindMutation.mutate({ path: { modelName } })}
						/>
						{modelViewModels.length === 0 ? <Text c="dimmed">No local models found.</Text> : null}
					</Stack>
				</Card>

				<DownloadProgressPanel
					inFlight={inFlightDownloads}
					downloadStatuses={downloadStatuses}
					onCancel={handleCancelDownload}
					cancellingModelName={cancelGgufDownloadMutation.isPending ? (cancelGgufDownloadMutation.variables ?? null) : null}
				/>

				<GgufBrowsePanel
					repositories={browseQueryResult.data ?? []}
					isLoading={browseQueryResult.isLoading && browseQuery.trim().length > 0}
					error={browseQueryResult.error}
					hasSearched={browseQuery.trim().length > 0}
					onSearch={setBrowseQuery}
					onDownload={handleBrowseDownload}
					downloadingRepoId={downloadingRepoId}
				/>
			</Stack>

			<ModelDetailsDialog
				opened={detailsModalOpened}
				onClose={closeDetailsModal}
				model={detailsModel}
				details={detailsQuery.data}
				detailsLoading={detailsQuery.isFetching}
				isActionPending={isActionPending}
				modelFitEnabled={nodeCapabilities.modelFit}
				onSetKind={(modelName, kind) => setKindMutation.mutate({ path: { modelName }, body: { kind } })}
				onResetKind={(modelName) => resetKindMutation.mutate({ path: { modelName } })}
			/>

			<GgufDownloadDialog
				repository={downloadRepo}
				onClose={() => setDownloadRepo(null)}
				onConfirm={handleConfirmQuantDownload}
				onConfirmDefault={handleConfirmDefaultDownload}
				isDownloading={startGgufDownloadMutation.isPending}
			/>
		</Container>
	);
}
