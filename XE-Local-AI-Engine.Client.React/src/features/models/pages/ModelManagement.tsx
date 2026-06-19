import { Alert, Badge, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconCloudDownload, IconRefresh, IconRobot } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";

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
import { InstalledModelsTable } from "@/features/models/components/InstalledModelsTable";
import { ModelDetailsDialog } from "@/features/models/components/ModelDetailsDialog";
import { PullModelDialog } from "@/features/models/components/PullModelDialog";
import { useModelPull } from "@/features/models/hooks/useModelPull";
import { toLocalModelViewModel } from "@/features/models/models/LocalModelMappers";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates */

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unexpected local model error";
}

export function ModelManagement() {
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	// The model whose details dialog is open (also the only model whose details endpoint is fetched).
	const [detailsModelName, setDetailsModelName] = useState<string | undefined>();
	const [pullModelName, setPullModelName] = useState("");
	const [detailsModalOpened, { open: openDetailsModal, close: closeDetailsModal }] = useDisclosure(false);
	const [pullModalOpened, { open: openPullModal, close: closePullModal }] = useDisclosure(false);
	// Shared single pull engine (invariant): the dialog's submit + live progress run through this hook, the same
	// one the recommendation Pull button uses. It owns the progress toast and installed-list invalidation.
	const modelPull = useModelPull();

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

	// Pull errors surface through the shared hook's progress toast; the other action errors surface via each
	// mutation's onError toast above (no inline banner).
	const isActionPending =
		selectMutation.isPending ||
		modelPull.isPulling ||
		deleteMutation.isPending ||
		setKindMutation.isPending ||
		resetKindMutation.isPending;
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

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							Worker Node
						</Text>
						<Title order={2}>Model management</Title>
						<Text c="dimmed">List, select, pull, and delete local Ollama models without changing runtime providers.</Text>
					</Stack>
					<Group gap="sm">
						<Badge color={modelsResponse?.isAvailable ? "green" : "red"}>
							{modelsResponse?.isAvailable ? "Ollama online" : "Ollama offline"}
						</Badge>
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => modelsQuery.refetch()}
							disabled={modelsQuery.isFetching}
						>
							Refresh
						</Button>
						<Button data-testid="open-pull-dialog-button" leftSection={<IconCloudDownload size={16} />} onClick={openPullModal}>
							Pull model
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

				{modelsResponse && !modelsResponse.isAvailable ? (
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						{modelsResponse.error ?? "Local Ollama is not available. Start Ollama to list and manage models."}
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
						{modelViewModels.length === 0 ? (
							<Text c="dimmed">{modelsResponse?.isAvailable ? "No local models found." : "Ollama is unavailable."}</Text>
						) : null}
					</Stack>
				</Card>
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

			<PullModelDialog
				opened={pullModalOpened}
				onClose={closePullModal}
				pullModelName={pullModelName}
				onPullModelNameChange={setPullModelName}
				onSubmit={() =>
					modelPull.pull(pullModelName, {
						// On a finished download, clear the typed name and close the dialog so reopening starts blank
						// (the close button is hidden mid-pull; an error keeps the dialog open with the value for retry).
						onSuccess: () => {
							setPullModelName("");
							closePullModal();
						},
					})
				}
				isPulling={modelPull.isPulling}
				isActionPending={isActionPending}
				progress={modelPull.progressPercent}
			/>
		</Container>
	);
}
