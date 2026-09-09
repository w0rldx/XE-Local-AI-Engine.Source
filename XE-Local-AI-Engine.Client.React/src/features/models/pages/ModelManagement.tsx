import { Button, Group, Loader, Text } from "@mantine/core";
import { IconBoxMultiple, IconFileImport, IconRefresh, IconRobot } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	deleteLocalModelMutation,
	deleteModelKindMutation,
	getLocalModelDetailsQueryKey,
	listLocalModelsOptions,
	listLocalModelsQueryKey,
	putModelKindMutation,
	selectLocalModelMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { DownloadProgressPanel } from "@/features/models/components/DownloadProgressPanel";
import { GgufBrowsePanel } from "@/features/models/components/GgufBrowsePanel";
import { GgufDownloadDialog } from "@/features/models/components/GgufDownloadDialog";
import { GgufImportDialog } from "@/features/models/components/GgufImportDialog";
import { ImportProgressPanel } from "@/features/models/components/ImportProgressPanel";
import { InstalledModelsTable } from "@/features/models/components/InstalledModelsTable";
import { ModelDetailsDialog } from "@/features/models/components/ModelDetailsDialog";
import { useGgufAcquisitionFlow } from "@/features/models/hooks/useGgufAcquisitionFlow";
import { useModelDetailsDialog } from "@/features/models/hooks/useModelDetailsDialog";
import { isInstalledLocalModel, toLocalModelViewModel } from "@/features/models/models/LocalModelMappers";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates -- Model mutations intentionally coordinate selection, dialogs, and result notifications in their user-event callbacks. */

function errorMessage(error: unknown, t: TFunction): string {
	return apiErrorMessage(error, t("pages.models.local.errors.unexpected", "Unexpected local model error"));
}

export function ModelManagement() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();

	// Reads run through the generated hey-api `*Options()` (which wire the shared axios instance + TanStack Query
	// AbortSignal automatically), wrapped in withResponseValidation so a zod response-shape failure surfaces as an
	// ApiError. The list query keeps the generated response envelope (isAvailable / selectedModelName / error) and
	// maps its optional-field items to the strict view-models in a memo. Invalidation uses the generated query-key
	// factories so every cached variant of an endpoint refetches.
	const {
		data: modelsResponse,
		isLoading: modelsIsLoading,
		error: modelsError,
		refetch: modelsRefetch,
		isFetching: modelsIsFetching,
	} = useQuery(withResponseValidation(listLocalModelsOptions()));
	// External-provider registrations ride the same list so the chat picker can offer them, but this page is the model
	// STORE: its table hands out Set default / Delete / Reset, none of which mean anything for a remote endpoint (the
	// delete endpoint answers 409, and D10 puts their whole lifecycle on the External providers page). Filtered out
	// before the view-models are built, so no action on this page can address one.
	const modelItems = useMemo(() => (modelsResponse?.items ?? []).filter(isInstalledLocalModel), [modelsResponse]);
	const modelViewModels = useMemo(() => modelItems.map(toLocalModelViewModel), [modelItems]);

	const detailsDialog = useModelDetailsDialog(modelsResponse?.isAvailable ?? false);
	const acquisition = useGgufAcquisitionFlow();

	const detailsModelName = detailsDialog.modelName;
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
			toast.success(t("pages.models.local.setDefaultSuccess", { name: selection.selectedModelName ?? "" }));
			await invalidateListAndDetails();
		},
		onError: (error) => toast.error(errorMessage(error, t)),
	});

	const deleteMutation = useMutation({
		...withResponseValidation(deleteLocalModelMutation()),
		onSuccess: async (response) => {
			toast.success(t("pages.models.local.deleteSuccess", { name: response.modelName ?? "" }));
			detailsDialog.clear();
			await invalidateListAndDetails();
		},
		onError: (error) => toast.error(errorMessage(error, t)),
	});

	const setKindMutation = useMutation({
		...withResponseValidation(putModelKindMutation()),
		// Setting an override does NOT probe Ollama, so the response's detectedKind may still be Unknown. Invalidate
		// the list so the next refetch runs lazy detection and the row reflects the freshly detected kind, not the
		// override response.
		onSuccess: async () => {
			await invalidateList();
		},
		onError: (error) => toast.error(errorMessage(error, t)),
	});

	const resetKindMutation = useMutation({
		...withResponseValidation(deleteModelKindMutation()),
		onSuccess: async () => {
			await invalidateList();
		},
		onError: (error) => toast.error(errorMessage(error, t)),
	});

	// Action errors surface via each mutation's onError toast above (no inline banner).
	const isActionPending =
		selectMutation.isPending || deleteMutation.isPending || setKindMutation.isPending || resetKindMutation.isPending;
	const detailsModel = modelViewModels.find((model) => model.modelName === detailsModelName);

	const confirmDelete = useCallback(
		async (modelName: string) => {
			const confirmed = await confirm({
				title: t("pages.models.local.delete.title", "Delete model"),
				description: t(
					"pages.models.local.delete.description",
					"Delete '{{name}}' from the local model store? This cannot be undone.",
					{ name: modelName },
				),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate({ path: { modelName } });
			}
		},
		[confirm, deleteMutation, t],
	);

	return (
		<PageShell>
			<PageHeader
				icon={<IconBoxMultiple size={24} />}
				title={t("pages.models.local.title", "Model management")}
				subtitle={t("pages.models.local.subtitle", "List, select, and delete installed local models.")}
				data-tour="models-overview"
				actions={
					<>
						{acquisition.importCapability.data?.available ? (
							<Button variant="light" leftSection={<IconFileImport size={16} />} onClick={acquisition.openImportModal}>
								{t("pages.models.gguf.import.action", "Import model")}
							</Button>
						) : null}
						<Button
							variant="subtle"
							leftSection={<IconRefresh size={16} />}
							onClick={() => modelsRefetch()}
							disabled={modelsIsFetching}
						>
							{t("common.refresh", "Refresh")}
						</Button>
					</>
				}
			/>

			{modelsIsLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.models.local.loading", "Loading local models…")}</Text>
				</Group>
			) : null}

			{modelsError ? <InlineErrorAlert message={errorMessage(modelsError, t)} /> : null}

			<SectionCard title={t("pages.models.local.installedTitle", "Installed models")} icon={<IconRobot size={22} />}>
				<InstalledModelsTable
					models={modelViewModels}
					isActionPending={isActionPending}
					onOpenDetails={detailsDialog.open}
					onSetDefault={(modelName) => selectMutation.mutate({ body: { modelName } })}
					onDelete={confirmDelete}
					onResetKind={(modelName) => resetKindMutation.mutate({ path: { modelName } })}
				/>
				{modelViewModels.length === 0 ? <EmptyState message={t("pages.models.local.empty", "No local models found.")} /> : null}
			</SectionCard>

			<DownloadProgressPanel
				inFlight={acquisition.inFlightDownloads}
				downloadStatuses={acquisition.downloadStatuses}
				onCancel={acquisition.handleCancelDownload}
				cancellingModelName={acquisition.cancellingModelName}
			/>

			<ImportProgressPanel
				operations={acquisition.importStatuses}
				onCancel={acquisition.handleCancelImport}
				cancellingOperationId={acquisition.cancellingOperationId}
			/>

			<GgufBrowsePanel
				repositories={acquisition.browseQueryResult.data ?? []}
				isLoading={acquisition.browseQueryResult.isLoading && acquisition.browseQuery.trim().length > 0}
				error={acquisition.browseQueryResult.error}
				hasSearched={acquisition.browseQuery.trim().length > 0}
				onSearch={acquisition.setBrowseQuery}
				onDownload={acquisition.handleBrowseDownload}
				downloadingRepoId={acquisition.downloadingRepoId}
			/>

			<ModelDetailsDialog
				opened={detailsDialog.opened}
				onClose={detailsDialog.close}
				model={detailsModel}
				details={detailsDialog.details}
				detailsLoading={detailsDialog.isFetching}
				isActionPending={isActionPending}
				modelFitEnabled={nodeCapabilities.modelFit}
				onSetKind={(modelName, kind) => setKindMutation.mutate({ path: { modelName }, body: { kind } })}
				onResetKind={(modelName) => resetKindMutation.mutate({ path: { modelName } })}
			/>

			<GgufDownloadDialog
				repository={acquisition.downloadRepo}
				onClose={acquisition.closeQuantPicker}
				onConfirm={acquisition.handleConfirmQuantDownload}
				onConfirmDefault={acquisition.handleConfirmDefaultDownload}
				isDownloading={acquisition.isDownloadStarting}
			/>

			<GgufImportDialog
				opened={acquisition.importModalOpened}
				onClose={acquisition.closeImportModal}
				onStarted={() => toast.success(t("pages.models.gguf.import.started", "Import started."))}
			/>
		</PageShell>
	);
}
