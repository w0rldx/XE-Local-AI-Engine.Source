import { useDisclosure } from "@mantine/hooks";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { defaultGgufQuant, type GgufRepository, type GgufRepositoryFile } from "@/features/models/models/GgufModels";
import {
	useActiveGgufAcquisitions,
	useCancelGgufImport,
	useGgufImportCapability,
} from "@/features/models/queries/useGgufAcquisitions";
import {
	useActiveGgufDownloads,
	useBrowseGgufRepositories,
	useCancelGgufDownload,
	useStartGgufDownload,
} from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

/* eslint-disable react-doctor/no-event-handler, react-doctor/no-chain-state-updates -- The acquisition actions intentionally coordinate the in-flight set, the quant dialog and the result notification in one user-event callback. */

/**
 * GGUF browse, quant-picked download and import: the whole model-acquisition half of the model management page.
 *
 * The committed browse term + the in-flight download set live in a shared store so they survive a remount AND so a
 * download handed off from the advisor's recommendation row becomes visible + cancellable here. The open-quant-picker
 * repo is page-local. useActiveGgufDownloads polls the backend for byte-level progress and reconciles the store so
 * downloads survive navigation/refresh — the backend list is the authoritative source of truth.
 */
export function useGgufAcquisitionFlow() {
	const { t } = useTranslation();
	const browseQuery = useGgufBrowseStore((state) => state.browseQuery);
	const setBrowseQuery = useGgufBrowseStore((state) => state.actions.setBrowseQuery);
	const inFlightDownloads = useGgufBrowseStore((state) => state.inFlightDownloads);
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	// Polls GET /downloads every second while any download is Running. Reconciles the store so entries started before
	// a page refresh rehydrate, and terminal entries (Completed/Cancelled/Failed) are removed from the store.
	const downloadStatuses = useActiveGgufDownloads();
	const acquisitionStatuses = useActiveGgufAcquisitions();
	const importStatuses = useMemo(
		() => [...acquisitionStatuses.values()].filter((status) => status.operationKind === "Import"),
		[acquisitionStatuses],
	);
	const importCapability = useGgufImportCapability();
	const cancelGgufImportMutation = useCancelGgufImport();
	const [importModalOpened, { open: openImportModal, close: closeImportModal }] = useDisclosure(false);
	// The repo whose quant picker dialog is open (null = closed). Selecting a browse row opens the dialog so the
	// operator picks the exact quant (incl. Unsloth Dynamic UD- quants) instead of always pulling the default Q4_K_M.
	const [downloadRepo, setDownloadRepo] = useState<GgufRepository | null>(null);

	const browseQueryResult = useBrowseGgufRepositories(browseQuery, true);
	const startGgufDownloadMutation = useStartGgufDownload();
	const cancelGgufDownloadMutation = useCancelGgufDownload();

	// Starts a GGUF download by repo id (the model name the backend resolves rides the response). On success the model
	// is tracked as in-flight (in the shared store); alreadyInFlight responses are surfaced too (already running).
	const startGgufDownload = useCallback(
		(repoId: string, fileName?: string, quant?: string, includeProjector?: boolean): void => {
			startGgufDownloadMutation.mutate(
				{ repoId, fileName, quant, includeProjector },
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
						toast.error(apiErrorMessage(error, t("pages.models.gguf.download.error", "Could not start the download."))),
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
	const handleConfirmQuantDownload = (repoId: string, file: GgufRepositoryFile, includeProjector?: boolean): void => {
		startGgufDownload(repoId, file.fileName, file.quant, includeProjector);
		setDownloadRepo(null);
	};

	// Fallback used when the picker has no files to offer (degraded/unreachable inspection): download the default quant
	// by repo id only, restoring the pre-picker one-click capability so a degraded inspect never blocks downloading.
	const handleConfirmDefaultDownload = (repoId: string, includeProjector?: boolean): void => {
		startGgufDownload(repoId, undefined, defaultGgufQuant, includeProjector);
		setDownloadRepo(null);
	};

	const handleCancelDownload = (modelName: string): void => {
		cancelGgufDownloadMutation.mutate(modelName, {
			onSuccess: () => {
				removeInFlight(modelName);
				toast.success(t("pages.models.gguf.download.cancelled", "Download cancelled."));
			},
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.models.gguf.download.cancelError", "Could not cancel the download."))),
		});
	};

	const handleCancelImport = (operationId: string): void => {
		cancelGgufImportMutation.mutate(operationId, {
			onError: () => toast.error(t("pages.models.gguf.import.cancelError", "Could not cancel the import.")),
		});
	};

	return {
		browseQuery,
		setBrowseQuery,
		browseQueryResult,
		inFlightDownloads,
		downloadStatuses,
		downloadRepo,
		closeQuantPicker: () => setDownloadRepo(null),
		handleBrowseDownload,
		handleConfirmQuantDownload,
		handleConfirmDefaultDownload,
		handleCancelDownload,
		isDownloadStarting: startGgufDownloadMutation.isPending,
		downloadingRepoId: startGgufDownloadMutation.isPending ? (startGgufDownloadMutation.variables?.repoId ?? null) : null,
		cancellingModelName: cancelGgufDownloadMutation.isPending ? (cancelGgufDownloadMutation.variables ?? null) : null,
		importCapability,
		importStatuses,
		importModalOpened,
		openImportModal,
		closeImportModal,
		handleCancelImport,
		cancellingOperationId: cancelGgufImportMutation.isPending ? (cancelGgufImportMutation.variables ?? null) : null,
	};
}
