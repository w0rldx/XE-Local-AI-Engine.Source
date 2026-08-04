import { ActionIcon, Alert, Badge, Button, Card, Group, Loader, Progress, Select, Stack, Switch, Tabs, Text, TextInput, Tooltip } from "@mantine/core";
import { IconCloudDownload, IconDownload, IconPlus, IconSparkles, IconTrash, IconX } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { type BrowseInstallRequest, ImageModelBrowsePanel } from "@/features/images/components/ImageModelBrowsePanel";
import { ImageModelCatalogPanel } from "@/features/images/components/ImageModelCatalogPanel";
import { useActiveImageModelDownloads } from "@/features/images/hooks/useActiveImageModelDownloads";
import {
	type ImageModelCatalogEntryView,
	type ImageModelDownloadView,
	type ImageModelFamily,
	imageModelFamilies,
	type ImageModelPartRole,
	imageModelPartRoles,
	type ImageModelView,
} from "@/features/images/models/ImageModels";
import {
	useCancelImageModelDownload,
	useDeleteImageModel,
	useImageModelCatalog,
	useRefreshInstalledImageModels,
	useStartImageModelDownload,
} from "@/features/images/queries/useImageQueries";
import { useDownloadRateEstimates } from "@/features/models/hooks/useDownloadRateEstimates";
import { formatDownloadEta, humanizeBytes } from "@/features/models/models/DownloadRateEstimate";

const families = imageModelFamilies;
const partRoles = imageModelPartRoles;

interface PartDraft {
	// Stable identity for the row, so removing a middle row does not remount the ones after it.
	id: string;
	role: ImageModelPartRole;
	fileName: string;
	// Blank = the set's repo. A file-set is not always published in one place: a Qwen-Image install takes its diffusion
	// weights and VAE from one repo and the Qwen2.5-VL text encoder from another.
	repoId: string;
	// Blank = unknown. A declared size is what makes the free-disk pre-flight run at all and what lets the backend
	// compute one aggregate percentage instead of a bar that restarts per part — on an 18 GB set that is the difference
	// between a usable progress display and a mystery.
	sizeBytes: string;
	sha256: string;
}

interface DownloadDraft {
	repoId: string;
	fileName: string;
	modelName: string;
	family: ImageModelFamily;
	// The simple form sends a single Diffusion part (correct and sufficient for SD1.5); the advanced one sends the
	// whole declared file-set.
	isAdvanced: boolean;
	parts: readonly PartDraft[];
}

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
// watching an indeterminate bar forever (F-031), and each in-flight row can be cancelled. Those progress rows and the
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

interface ManualDownloadFormProps {
	draft: DownloadDraft;
	setDraft: React.Dispatch<React.SetStateAction<DownloadDraft>>;
	advancedParts: readonly PartDraft[];
	hasDiffusionPart: boolean;
	canSubmit: boolean;
	isDraftInFlight: boolean;
	isSubmitting: boolean;
	onSubmit: () => void;
	onUpdatePart: (id: string, patch: Partial<PartDraft>) => void;
	onAddPart: () => void;
	onRemovePart: (id: string) => void;
}

// The manual escape hatch, unchanged in behaviour. It stays because discovery cannot cover everything: a brand-new
// repo the Hub has not tagged text-to-image yet, or a private mirror, is still installable by typing it in.
function ManualDownloadForm({
	draft,
	setDraft,
	hasDiffusionPart,
	canSubmit,
	isDraftInFlight,
	isSubmitting,
	onSubmit,
	onUpdatePart,
	onAddPart,
	onRemovePart,
}: ManualDownloadFormProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs">
			<TextInput
					label={t("pages.images.models.download.repoId.label", "Hugging Face repo")}
					placeholder="Qwen/Qwen-Image-GGUF"
					value={draft.repoId}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setDraft((current) => ({ ...current, repoId: value }));
					}}
					data-testid="image-model-download-repo"
				/>
				{draft.isAdvanced ? null : (
					<TextInput
						label={t("pages.images.models.download.fileName.label", "Weight file")}
						placeholder="sd-v1-5.q8_0.gguf"
						value={draft.fileName}
						onChange={(event) => {
							const value = event.currentTarget.value;
							setDraft((current) => ({ ...current, fileName: value }));
						}}
						data-testid="image-model-download-file"
					/>
				)}
				<TextInput
					label={t("pages.images.models.download.modelName.label", "Model name")}
					placeholder="sd-1.5"
					value={draft.modelName}
					onChange={(event) => {
						const value = event.currentTarget.value;
						setDraft((current) => ({ ...current, modelName: value }));
					}}
					data-testid="image-model-download-name"
				/>
				<Select
					label={t("pages.images.models.download.family.label", "Family")}
					data={families.map((family) => ({ value: family, label: t(`pages.images.models.families.${family}`, family) }))}
					value={draft.family}
					allowDeselect={false}
					onChange={(value) => setDraft((current) => ({ ...current, family: (value ?? current.family) as ImageModelFamily }))}
					data-testid="image-model-download-family"
				/>
				<Switch
					label={t("pages.images.models.download.advanced.toggle", "Advanced: multi-part file set")}
					description={t(
						"pages.images.models.download.advanced.toggleDescription",
						"SDXL, SD3, FLUX and Qwen-Image ship as several files (diffusion weights, VAE, text encoders) instead of one.",
					)}
					checked={draft.isAdvanced}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						setDraft((current) => ({ ...current, isAdvanced: checked }));
					}}
					data-testid="image-model-download-advanced-toggle"
				/>
				{draft.isAdvanced ? (
					<Stack gap="sm" data-testid="image-model-download-parts">
						{draft.parts.map((part, index) => (
							<PartRow
								key={part.id}
								part={part}
								index={index}
								canRemove={draft.parts.length > 1}
								onChange={onUpdatePart}
								onRemove={onRemovePart}
							/>
						))}
						<Group justify="space-between">
							<Button
								size="xs"
								variant="light"
								leftSection={<IconPlus size={14} />}
								onClick={onAddPart}
								data-testid="image-model-download-add-part"
							>
								{t("pages.images.models.download.advanced.addPart", "Add file")}
							</Button>
							{hasDiffusionPart ? null : (
								<Text size="xs" c="red" data-testid="image-model-download-parts-warning">
									{t("pages.images.models.download.advanced.diffusionRequired", "A file set needs one Diffusion file.")}
								</Text>
							)}
						</Group>
						<Alert variant="light" color="gray" data-testid="image-model-download-hint">
							{t(
								"pages.images.models.download.advanced.hint",
								"Leave a file's repository blank to use the one above — a set can span repositories. Sizes are optional but enable the free-space check and one combined progress bar.",
							)}
						</Alert>
					</Stack>
				) : (
					<Alert variant="light" color="gray" data-testid="image-model-download-hint">
						{t(
							"pages.images.models.download.hint",
							"One weight file, for single-file models like SD1.5. Turn on Advanced for a multi-part model.",
						)}
					</Alert>
				)}
				<Group justify="flex-end">
					<Button
						leftSection={<IconDownload size={16} />}
						loading={isSubmitting}
						disabled={!canSubmit || isDraftInFlight}
						onClick={onSubmit}
						data-testid="image-model-download-submit"
					>
						{t("pages.images.models.download.submit", "Download")}
					</Button>
				</Group>
		</Stack>
	);
}

interface PartRowProps {
	part: PartDraft;
	index: number;
	canRemove: boolean;
	onChange: (id: string, patch: Partial<PartDraft>) => void;
	onRemove: (id: string) => void;
}

// One declared file of a multi-part set. Only the role and file name are required; the repository override, size and
// digest are the fields that make a real cross-repo install work and its progress honest, so they are visible rather
// than hidden behind another disclosure.
function PartRow({ part, index, canRemove, onChange, onRemove }: PartRowProps) {
	const { t } = useTranslation();

	return (
		<Card withBorder={true} padding="sm" radius="sm" data-testid={`image-model-download-part-${index}`}>
			<Stack gap="xs">
				<Group align="flex-end" wrap="nowrap" gap="xs">
					<Select
						label={t("pages.images.models.download.advanced.role.label", "Role")}
						data={partRoles.map((role) => ({ value: role, label: t(`pages.images.models.partRoles.${role}`, role) }))}
						value={part.role}
						allowDeselect={false}
						w={150}
						onChange={(value) => onChange(part.id, { role: (value ?? part.role) as ImageModelPartRole })}
						data-testid={`image-model-download-part-role-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.fileName.label", "File")}
						placeholder="Qwen_Image-Q4_K_M.gguf"
						value={part.fileName}
						style={{ flex: 1 }}
						onChange={(event) => onChange(part.id, { fileName: event.currentTarget.value })}
						data-testid={`image-model-download-part-file-${index}`}
					/>
					<Tooltip label={t("pages.images.models.download.advanced.removePart", "Remove file")}>
						<ActionIcon
							variant="light"
							color="red"
							aria-label={t("pages.images.models.download.advanced.removePart", "Remove file")}
							disabled={!canRemove}
							onClick={() => onRemove(part.id)}
							data-testid={`image-model-download-part-remove-${index}`}
						>
							<IconX size={16} />
						</ActionIcon>
					</Tooltip>
				</Group>
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.images.models.download.advanced.repoId.label", "Repository (optional)")}
						placeholder={t("pages.images.models.download.advanced.repoId.placeholder", "Same as above")}
						value={part.repoId}
						onChange={(event) => onChange(part.id, { repoId: event.currentTarget.value })}
						data-testid={`image-model-download-part-repo-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.sizeBytes.label", "Size in bytes (optional)")}
						placeholder="13065746976"
						inputMode="numeric"
						value={part.sizeBytes}
						onChange={(event) => onChange(part.id, { sizeBytes: event.currentTarget.value })}
						data-testid={`image-model-download-part-size-${index}`}
					/>
					<TextInput
						label={t("pages.images.models.download.advanced.sha256.label", "SHA-256 (optional)")}
						value={part.sha256}
						onChange={(event) => onChange(part.id, { sha256: event.currentTarget.value })}
						data-testid={`image-model-download-part-sha-${index}`}
					/>
				</Group>
			</Stack>
		</Card>
	);
}

interface DownloadRowProps {
	modelName: string;
	status: ImageModelDownloadView | undefined;
	etaSeconds: number | undefined;
	bytesPerSecond: number | undefined;
	isCancelling: boolean;
	onCancel: (modelName: string) => void;
}

// One in-flight file-set pull. A percentage is shown ONLY when the set total is known — every part must have declared a
// size for the backend to compute one — because a bar derived from a partial total would pass 100% and read as broken.
// Without a total the row still advances honestly: bytes transferred, the part being fetched, and the measured speed.
function DownloadRow({ modelName, status, etaSeconds, bytesPerSecond, isCancelling, onCancel }: DownloadRowProps) {
	const { t } = useTranslation();

	const completedBytes = status?.completedBytes ?? null;
	const totalBytes = status?.totalBytes ?? null;
	const percent =
		totalBytes != null && totalBytes > 0 && completedBytes != null ? Math.min(100, Math.round((completedBytes / totalBytes) * 100)) : null;

	const partLabel =
		status?.partIndex != null && status.partCount != null && status.partCount > 1
			? t("pages.images.models.download.part", "Part {{index}} of {{count}}", { index: status.partIndex, count: status.partCount })
			: undefined;
	const speedLabel =
		bytesPerSecond !== undefined
			? t("pages.images.models.download.speed", "{{value}}/s", { value: humanizeBytes(bytesPerSecond) })
			: undefined;
	const etaDuration = formatDownloadEta(etaSeconds);
	const etaLabel = etaDuration ? t("pages.images.models.download.eta", "~{{duration}} left", { duration: etaDuration }) : undefined;

	// The headline: a percentage when one is computable, otherwise the bytes that HAVE moved. Never a fabricated
	// percentage — the operator reading "100%" on a bar that keeps moving is the failure this guards against.
	let headline = t("pages.images.models.download.starting", "Starting…");
	if (percent !== null) {
		headline = t("pages.images.models.download.progress", "Downloading… {{percent}}%", { percent });
	} else if (completedBytes != null) {
		headline = t("pages.images.models.download.transferred", "{{completed}} transferred", { completed: humanizeBytes(completedBytes) });
	}

	const bytesLabel =
		percent !== null && completedBytes != null && totalBytes != null
			? t("pages.images.models.download.bytesOf", "{{completed}} / {{total}}", {
					completed: humanizeBytes(completedBytes),
					total: humanizeBytes(totalBytes),
				})
			: undefined;

	return (
		<Stack gap={4} data-testid={`image-model-download-row-${modelName}`}>
			<Group justify="space-between" wrap="nowrap" align="center">
				<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
					{percent === null ? <Loader size="xs" /> : null}
					<Text size="sm" fw={500} truncate={true}>
						{modelName}
					</Text>
				</Group>
				<Button
					size="xs"
					variant="light"
					color="red"
					leftSection={<IconX size={14} />}
					loading={isCancelling}
					disabled={isCancelling}
					onClick={() => onCancel(modelName)}
					data-testid={`image-model-download-cancel-${modelName}`}
				>
					{t("pages.images.models.download.cancel", "Cancel")}
				</Button>
			</Group>
			<Progress
				value={percent ?? 100}
				striped={percent === null}
				animated={percent === null}
				size="sm"
				radius="sm"
				aria-label={t("pages.images.models.download.progressAriaLabel", "Download progress")}
				data-testid={`image-model-download-bar-${modelName}`}
			/>
			<Text size="xs" c="dimmed" data-testid={`image-model-download-detail-${modelName}`}>
				{[headline, bytesLabel, partLabel, speedLabel, etaLabel].filter(Boolean).join(" · ")}
			</Text>
		</Stack>
	);
}
