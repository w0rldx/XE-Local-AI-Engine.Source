import { ActionIcon, Alert, Badge, Card, Group, Loader, Stack, Tabs, Text, Tooltip } from "@mantine/core";
import { IconCloudDownload, IconPlus, IconSparkles, IconTrash } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { type BrowseInstallRequest, ImageModelBrowsePanel } from "@/features/images/components/ImageModelBrowsePanel";
import { ImageModelCatalogPanel } from "@/features/images/components/ImageModelCatalogPanel";
import { DownloadRow } from "@/features/images/components/ImageDownloadRow";
import { ManualDownloadForm } from "@/features/images/components/ImageManualDownloadForm";
import { useActiveImageModelDownloads } from "@/features/images/hooks/useActiveImageModelDownloads";
import type {
	DownloadDraft,
	ImageModelCatalogEntryView,
	ImageModelFamily,
	ImageModelPartRole,
	ImageModelView,
	PartDraft,
} from "@/features/images/models/ImageModels";
import {
	useCancelImageModelDownload,
	useDeleteImageModel,
	useImageModelCatalog,
	useRefreshInstalledImageModels,
	useStartImageModelDownload,
} from "@/features/images/queries/useImageQueries";
import { useDownloadRateEstimates } from "@/features/models/hooks/useDownloadRateEstimates";
import { humanizeBytes } from "@/features/models/models/DownloadRateEstimate";

let nextPartId = 0;

function newPart(role: ImageModelPartRole): PartDraft {
	nextPartId += 1;
	return { id: `part-${nextPartId}`, role, fileName: "", repoId: "", sizeBytes: "", sha256: "" };
}

const emptyDraft: DownloadDraft = {
	repoId: "",
	fileName: "",
	modelName: "",
	family: "Sd15",
	isAdvanced: false,
	parts: [newPart("Diffusion"), newPart("Vae")],
};

// Parses an optional byte count. Anything that is not a positive integer is reported as "not declared" rather than
// coerced to 0 — a zero would look like a declared size and silently disable the disk pre-flight it was meant to feed.
function parseOptionalSize(raw: string): number | undefined {
	const trimmed = raw.trim();
	if (trimmed === "") {
		return undefined;
	}
	const parsed = Number(trimmed);
	return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : undefined;
}

interface ImageModelManagerProps {
	models: readonly ImageModelView[];
	isLoading: boolean;
	// Notifies the page that at least one detached download is (or is no longer) in flight, so it can poll
	// listImageModels for the models appearing on completion. See useImageModels(pollWhilePending).
	onPendingDownloadChange?: (pending: boolean) => void;
}

// Image-model management. Lists installed image models (with a confirmed delete to reclaim the disk a multi-gigabyte
// file-set occupies) and offers three ways to add one, in increasing order of effort: a curated catalog (one click,
// nothing typed), a Hugging Face browse → pick-files flow, and the original manual form.
//
// The manual form is kept rather than replaced. Discovery covers the Hub's text-to-image facet, which is most of what
// anyone wants and none of what a brand-new or untagged repo offers — without the escape hatch, a model the Hub has
// not classified yet becomes uninstallable.
//
// The download itself runs on the backend coordinator, which records a terminal phase for every attempt; while any are
// in flight this component polls their status so a FAILED download surfaces its reason instead of leaving the operator
// watching an indeterminate bar forever, and each in-flight row can be cancelled. Those progress rows and the
// per-model error alerts sit ABOVE the tabs, so switching tab mid-download never hides a running transfer.
export function ImageModelManager({ models, isLoading, onPendingDownloadChange }: ImageModelManagerProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const [draft, setDraft] = useState<DownloadDraft>(emptyDraft);
	// Keyed by model name: several downloads can run at once, so one shared error slot would hide all but the last.
	const [downloadErrors, setDownloadErrors] = useState<Readonly<Record<string, string>>>({});
	const [cancellingModelName, setCancellingModelName] = useState<string | null>(null);
	const [deletingModelName, setDeletingModelName] = useState<string | null>(null);
	// The catalog entry whose Install button was clicked, so only that row spins while the 202 is in flight.
	const [installingCatalogId, setInstallingCatalogId] = useState<string | null>(null);
	// Model names whose terminal phase has already been reacted to (see the reconcile effect below).
	const handledTerminals = useRef<Set<string>>(new Set());

	const downloadMutation = useStartImageModelDownload();
	const cancelMutation = useCancelImageModelDownload();
	const deleteMutation = useDeleteImageModel();
	const { statuses, inFlight, track, untrack } = useActiveImageModelDownloads();
	const refreshInstalledModels = useRefreshInstalledImageModels();
	// Poll the catalog while anything is transferring: its installed flag only becomes true once the download actually
	// finishes, long after the 202 that invalidated it.
	const catalogQuery = useImageModelCatalog(inFlight.length > 0);
	// Client-derived speed + ETA: the status carries byte counts but no timestamps, so the rate comes from successive
	// polls. On an 18 GB file-set this is the number that tells the operator whether to wait or walk away.
	const rateEstimates = useDownloadRateEstimates(statuses);

	// The advanced form's file-set is valid once the diffusion part names a file — every other role is optional, and
	// which ones a family actually needs is the model author's business, not something to hard-code per family here.
	const advancedParts = draft.parts.filter((part) => part.fileName.trim().length > 0);
	const hasDiffusionPart = advancedParts.some((part) => part.role === "Diffusion");
	const canSubmit =
		draft.repoId.trim().length > 0 &&
		draft.modelName.trim().length > 0 &&
		(draft.isAdvanced ? hasDiffusionPart : draft.fileName.trim().length > 0);
	const isDraftInFlight = inFlight.includes(draft.modelName.trim());
	const installedModelNames = useMemo(() => models.map((model) => model.modelName), [models]);
	// A catalog row is busy while its 202 is in flight AND for as long as the transfer it started is running.
	const busyCatalogIds = useMemo(
		() => (installingCatalogId === null ? inFlight : [...new Set([installingCatalogId, ...inFlight])]),
		[installingCatalogId, inFlight],
	);

	// Resolve each tracked download the moment the backend reports a terminal phase. A failure raises a toast AND leaves
	// an inline reason on the card; a success/cancel just drops the row. Without this the UI could only ever observe
	// success (the model appearing), which is precisely why a typo used to hang forever.
	//
	// `untrack` only queues a state update, so a poll landing before it flushes would re-deliver the same terminal
	// status and raise a duplicate toast. The handled set makes each terminal phase fire exactly once per model.
	useEffect(() => {
		for (const status of statuses.values()) {
			if (status.phase === "Running" || handledTerminals.current.has(status.modelName)) {
				continue;
			}
			handledTerminals.current.add(status.modelName);
			if (status.phase === "Failed") {
				const reason = status.sanitizedError ?? t("pages.images.models.download.failed", "The model download failed.");
				setDownloadErrors((current) => ({ ...current, [status.modelName]: reason }));
				toast.error(reason);
			} else if (status.phase === "Completed") {
				// Refresh BEFORE untracking. Untracking empties `inFlight`, which switches the installed-model and
				// catalog queries back off — and the 2s status poll routinely sees Completed before the 5s model poll
				// has fired at all. Relying on those polls alone therefore loses the race often enough to matter: the
				// model that just finished installing stays invisible until some unrelated refetch happens along.
				refreshInstalledModels();
			}
			untrack(status.modelName);
		}
	}, [statuses, untrack, t, refreshInstalledModels]);

	// Belt-and-braces: stop tracking once the downloaded model surfaces in the polled list, even if the status registry
	// was lost (a node restart drops it) and the terminal phase above never arrives.
	useEffect(() => {
		for (const model of models) {
			untrack(model.modelName);
		}
	}, [models, untrack]);

	// Keep the page's poll flag in sync with whether any download is in flight.
	useEffect(() => {
		onPendingDownloadChange?.(inFlight.length > 0);
	}, [inFlight, onPendingDownloadChange]);

	// The single install path every entry point funnels through — the curated catalog, the Hugging Face picker and the
	// manual form all post the same file-set shape. Keeping one implementation is what stops the catalog's one-click
	// install from drifting away from the tracking/error handling the manual form already got right.
	const startDownload = useCallback(
		(payload: {
			modelName: string;
			repoId: string;
			family: ImageModelFamily;
			parts: readonly {
				role: ImageModelPartRole;
				fileName: string;
				repoId?: string;
				sizeBytes?: number;
				sha256?: string;
			}[];
		},
		onStarted?: () => void) => {
			const { modelName } = payload;
			setDownloadErrors((current) => {
				const { [modelName]: _removed, ...rest } = current;
				return rest;
			});
			// A retry of the same name must be able to report its OWN terminal phase; without this the second attempt's
			// failure would be swallowed by the first attempt's entry.
			handledTerminals.current.delete(modelName);

			downloadMutation.mutate(
				{
					modelName,
					repoId: payload.repoId,
					family: payload.family,
					parts: payload.parts.map((part) => ({ ...part })),
				},
				{
					onSuccess: () => {
						track(modelName);
						toast.success(t("pages.images.models.download.started", "Download started. The model will appear once ready."));
						onStarted?.();
					},
					onError: (error) => {
						const message =
							error instanceof ApiError && error.message
								? error.message
								: t("pages.images.models.download.error", "Could not start the model download.");
						toast.error(message);
					},
					// Clears the per-row catalog spinner on BOTH outcomes; a rejected start that left the button
					// spinning would look like a download that never reports.
					onSettled: () => setInstallingCatalogId(null),
				},
			);
		},
		[downloadMutation, t, track],
	);

	const handleDownload = useCallback(() => {
		if (!canSubmit) {
			return;
		}
		const parts = draft.isAdvanced
			? advancedParts.map((part) => ({
					role: part.role,
					fileName: part.fileName.trim(),
					repoId: part.repoId.trim() === "" ? undefined : part.repoId.trim(),
					sizeBytes: parseOptionalSize(part.sizeBytes),
					sha256: part.sha256.trim() === "" ? undefined : part.sha256.trim(),
				}))
			: [{ role: "Diffusion" as ImageModelPartRole, fileName: draft.fileName.trim() }];

		startDownload(
			{
				modelName: draft.modelName.trim(),
				repoId: draft.repoId.trim(),
				family: draft.family,
				parts,
			},
			() =>
				// Clear the entered values but stay in the mode and family the operator chose — a second multi-part
				// install almost always follows the first, and silently dropping back to the simple form loses it.
				setDraft((current) => ({
					...emptyDraft,
					isAdvanced: current.isAdvanced,
					family: current.family,
					parts: [newPart("Diffusion"), newPart("Vae")],
				})),
		);
	}, [advancedParts, canSubmit, draft, startDownload]);

	// A catalog row already carries the exact file-set (including the per-part repository overrides a cross-repo set
	// needs) and verified sizes, so installing it is a straight pass-through with nothing typed.
	const handleCatalogInstall = useCallback(
		(entry: ImageModelCatalogEntryView) => {
			setInstallingCatalogId(entry.id);
			startDownload({
				modelName: entry.id,
				repoId: entry.repoId,
				family: entry.family,
				parts: entry.parts.map((part) => ({
					role: part.role,
					fileName: part.fileName,
					repoId: part.repoId ?? undefined,
					sizeBytes: part.sizeBytes,
				})),
			});
		},
		[startDownload],
	);

	const handleBrowseInstall = useCallback(
		(request: BrowseInstallRequest) => {
			startDownload({
				modelName: request.modelName,
				repoId: request.repoId,
				family: request.family,
				parts: request.parts.map((part) => ({ ...part })),
			});
		},
		[startDownload],
	);

	const updatePart = useCallback((id: string, patch: Partial<PartDraft>) => {
		setDraft((current) => ({
			...current,
			parts: current.parts.map((part) => (part.id === id ? { ...part, ...patch } : part)),
		}));
	}, []);

	const addPart = useCallback(() => {
		setDraft((current) => ({ ...current, parts: [...current.parts, newPart("Llm")] }));
	}, []);

	const removePart = useCallback((id: string) => {
		setDraft((current) => ({ ...current, parts: current.parts.filter((part) => part.id !== id) }));
	}, []);

	const handleCancel = useCallback(
		(modelName: string) => {
			setCancellingModelName(modelName);
			cancelMutation.mutate(modelName, {
				onError: (error) => {
					const message =
						error instanceof ApiError && error.message
							? error.message
							: t("pages.images.models.download.cancelError", "Could not cancel the download.");
					toast.error(message);
				},
				onSettled: () => setCancellingModelName(null),
			});
		},
		[cancelMutation, t],
	);

	const handleDelete = useCallback(
		async (modelName: string) => {
			const confirmed = await confirm({
				title: t("pages.images.models.delete.title", "Delete image model"),
				description: t("pages.images.models.delete.description", "Delete '{{modelName}}' and its weight files? This cannot be undone.", {
					modelName,
				}),
				confirmationText: t("pages.images.models.delete.confirm", "Delete"),
				cancellationText: t("pages.images.models.delete.cancel", "Cancel"),
			});
			if (!confirmed) {
				return;
			}

			setDeletingModelName(modelName);
			deleteMutation.mutate(modelName, {
				onSuccess: () => toast.success(t("pages.images.models.delete.deleted", "Model deleted.")),
				onError: (error) => {
					const message =
						error instanceof ApiError && error.message
							? error.message
							: t("pages.images.models.delete.error", "Could not delete the model.");
					toast.error(message);
				},
				onSettled: () => setDeletingModelName(null),
			});
		},
		[confirm, deleteMutation, t],
	);

	return (
		<Stack gap="md" data-testid="image-model-manager">
			<Stack gap="xs">
				<Text fw={600}>{t("pages.images.models.installedTitle", "Installed image models")}</Text>
				{isLoading ? (
					<Loader size="sm" data-testid="image-models-loading" />
				) : models.length === 0 ? (
					<Text c="dimmed" data-testid="image-models-empty">
						{t("pages.images.models.empty", "No image models installed yet.")}
					</Text>
				) : (
					<Stack gap="xs" data-testid="image-models-list">
						{models.map((model) => (
							<Card key={model.modelName} withBorder={true} padding="sm" radius="sm">
								<Group justify="space-between" wrap="nowrap">
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text size="sm" fw={500} truncate={true}>
											{model.modelName}
										</Text>
										<Text size="xs" c="dimmed" truncate={true}>
											{model.repoId}
										</Text>
									</Stack>
									<Group gap="xs" wrap="nowrap">
										<Text size="xs" c="dimmed">
											{humanizeBytes(model.sizeBytes)}
										</Text>
										<Badge variant="light">{t(`pages.images.models.families.${model.family}`, model.family)}</Badge>
										<Tooltip label={t("pages.images.models.delete.action", "Delete model")}>
											<ActionIcon
												variant="light"
												color="red"
												aria-label={t("pages.images.models.delete.action", "Delete model")}
												loading={deletingModelName === model.modelName}
												disabled={deletingModelName === model.modelName}
												onClick={() => handleDelete(model.modelName)}
												data-testid={`image-model-delete-${model.modelName}`}
											>
												<IconTrash size={16} />
											</ActionIcon>
										</Tooltip>
									</Group>
								</Group>
							</Card>
						))}
					</Stack>
				)}
			</Stack>

			{inFlight.length > 0 ? (
				<Stack gap="sm" data-testid="image-model-download-progress">
					{inFlight.map((modelName) => (
						<DownloadRow
							key={modelName}
							modelName={modelName}
							status={statuses.get(modelName)}
							etaSeconds={rateEstimates.get(modelName)?.etaSeconds}
							bytesPerSecond={rateEstimates.get(modelName)?.bytesPerSecond}
							isCancelling={cancellingModelName === modelName}
							onCancel={handleCancel}
						/>
					))}
				</Stack>
			) : null}

			{Object.entries(downloadErrors).map(([modelName, reason]) => (
				<Alert
					key={modelName}
					variant="light"
					color="red"
					withCloseButton={true}
					closeButtonLabel={t("pages.images.models.download.dismissError", "Dismiss")}
					onClose={() =>
						setDownloadErrors((current) => {
							const { [modelName]: _removed, ...rest } = current;
							return rest;
						})
					}
					data-testid="image-model-download-error"
				>
					{reason}
				</Alert>
			))}

			<Tabs defaultValue="catalog" keepMounted={false} data-testid="image-model-add-tabs">
				<Tabs.List>
					<Tabs.Tab value="catalog" leftSection={<IconSparkles size={14} />} data-testid="image-model-tab-catalog">
						{t("pages.images.models.tabs.catalog", "Recommended")}
					</Tabs.Tab>
					<Tabs.Tab value="browse" leftSection={<IconCloudDownload size={14} />} data-testid="image-model-tab-browse">
						{t("pages.images.models.tabs.browse", "Hugging Face")}
					</Tabs.Tab>
					<Tabs.Tab value="manual" leftSection={<IconPlus size={14} />} data-testid="image-model-tab-manual">
						{t("pages.images.models.tabs.manual", "Advanced")}
					</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="catalog" pt="md">
					<ImageModelCatalogPanel
						entries={catalogQuery.data ?? []}
						isLoading={catalogQuery.isPending}
						error={catalogQuery.error}
						busyEntryIds={busyCatalogIds}
						onInstall={handleCatalogInstall}
					/>
				</Tabs.Panel>

				<Tabs.Panel value="browse" pt="md">
					<ImageModelBrowsePanel
						installedModelNames={installedModelNames}
						isInstalling={downloadMutation.isPending}
						onInstall={handleBrowseInstall}
					/>
				</Tabs.Panel>

				<Tabs.Panel value="manual" pt="md">
					<ManualDownloadForm
						draft={draft}
						setDraft={setDraft}
						advancedParts={advancedParts}
						hasDiffusionPart={hasDiffusionPart}
						canSubmit={canSubmit}
						isDraftInFlight={isDraftInFlight}
						isSubmitting={downloadMutation.isPending}
						onSubmit={handleDownload}
						onUpdatePart={updatePart}
						onAddPart={addPart}
						onRemovePart={removePart}
					/>
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);
}
