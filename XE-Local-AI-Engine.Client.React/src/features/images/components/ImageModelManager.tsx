import { Alert, Badge, Button, Card, Group, Loader, Progress, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconDownload } from "@tabler/icons-react";
import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { toast } from "@/core/ui/notifications/Toast";
import type { ImageModelView } from "@/features/images/models/ImageModels";
import { useImageModelDownloads, useStartImageModelDownload } from "@/features/images/queries/useImageQueries";

// Diffusion families the download form offers. Must match the backend ImageModelFamily enum names (parsed
// case-insensitively). Step 1 targets SD1.5 (single-file); the others are admitted but need extra parts — the minimal
// form here sends a single Diffusion part, which is complete for SD1.5 and the seed for a later multi-part UI.
const families = ["Sd15", "Sdxl", "Sd3", "Flux"] as const;
type ImageModelFamily = (typeof families)[number];

interface DownloadDraft {
	repoId: string;
	fileName: string;
	modelName: string;
	family: ImageModelFamily;
}

const emptyDraft: DownloadDraft = { repoId: "", fileName: "", modelName: "", family: "Sd15" };

interface ImageModelManagerProps {
	models: readonly ImageModelView[];
	isLoading: boolean;
	// Notifies the page that a detached download is (or is no longer) in flight, so it can poll listImageModels for the
	// model appearing on completion. See useImageModels(pollWhilePending).
	onPendingDownloadChange?: (pending: boolean) => void;
}

// Minimal image-model management. Lists installed image models and offers a "download model" action (202 accepted) that
// starts a single-file (Diffusion-part) weight download. The download itself runs on the backend coordinator, which
// records a terminal phase for every attempt; while one is pending this component polls that status so a FAILED
// download surfaces its reason instead of leaving the operator watching an indeterminate bar forever (F-031).
export function ImageModelManager({ models, isLoading, onPendingDownloadChange }: ImageModelManagerProps) {
	const { t } = useTranslation();
	const [draft, setDraft] = useState<DownloadDraft>(emptyDraft);
	const [pendingModelName, setPendingModelName] = useState<string | null>(null);
	const [downloadError, setDownloadError] = useState<string | null>(null);
	const downloadMutation = useStartImageModelDownload();

	const canSubmit = draft.repoId.trim().length > 0 && draft.fileName.trim().length > 0 && draft.modelName.trim().length > 0;
	const isDownloadPending = pendingModelName !== null;

	const downloadsQuery = useImageModelDownloads(isDownloadPending);
	const pendingDownload = downloadsQuery.data?.find((entry) => entry.modelName === pendingModelName);

	// Real percentage once the source reported a content length; null keeps the indeterminate bar.
	const downloadPercent =
		pendingDownload?.totalBytes != null && pendingDownload.totalBytes > 0 && pendingDownload.completedBytes != null
			? Math.min(100, Math.round((pendingDownload.completedBytes / pendingDownload.totalBytes) * 100))
			: null;

	// Resolve the pending download the moment the backend reports a terminal phase. A failure raises a toast AND leaves
	// an inline reason on the card; a success/cancel just clears the indicator. Without this the UI could only ever
	// observe success (the model appearing), which is precisely why a typo used to hang forever.
	useEffect(() => {
		if (pendingModelName === null || pendingDownload === undefined) {
			return;
		}
		if (pendingDownload.phase === "Failed") {
			setDownloadError(pendingDownload.sanitizedError ?? t("pages.images.models.download.failed", "The model download failed."));
			toast.error(pendingDownload.sanitizedError ?? t("pages.images.models.download.failed", "The model download failed."));
			setPendingModelName(null);
			return;
		}
		if (pendingDownload.phase === "Completed" || pendingDownload.phase === "Cancelled") {
			setPendingModelName(null);
		}
	}, [pendingDownload, pendingModelName, t]);

	// Belt-and-braces: clear the indicator once the downloaded model surfaces in the polled list, even if the status
	// registry was lost (a node restart drops it) and the terminal phase above never arrives.
	useEffect(() => {
		if (pendingModelName !== null && models.some((model) => model.modelName === pendingModelName)) {
			setPendingModelName(null);
		}
	}, [models, pendingModelName]);

	// Keep the page's poll flag in sync with whether a download is in flight.
	useEffect(() => {
		onPendingDownloadChange?.(isDownloadPending);
	}, [isDownloadPending, onPendingDownloadChange]);

	const handleDownload = useCallback(() => {
		if (!canSubmit) {
			return;
		}
		const modelName = draft.modelName.trim();
		setDownloadError(null);
		downloadMutation.mutate(
			{
				modelName,
				repoId: draft.repoId.trim(),
				family: draft.family,
				parts: [{ role: "Diffusion", fileName: draft.fileName.trim() }],
			},
			{
				onSuccess: () => {
					setPendingModelName(modelName);
					toast.success(t("pages.images.models.download.started", "Download started. The model will appear once ready."));
					setDraft(emptyDraft);
				},
				onError: (error) => {
					const message =
						error instanceof ApiError && error.message
							? error.message
							: t("pages.images.models.download.error", "Could not start the model download.");
					toast.error(message);
				},
			},
		);
	}, [canSubmit, downloadMutation, draft, t]);

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
									<Badge variant="light">{model.family}</Badge>
								</Group>
							</Card>
						))}
					</Stack>
				)}
			</Stack>

			<Stack gap="xs">
				<Text fw={600}>{t("pages.images.models.download.title", "Download a model")}</Text>
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
				<Alert variant="light" color="gray" data-testid="image-model-download-hint">
					{t("pages.images.models.download.hint", "Single-file (SD1.5-style) weights only. Multi-part models are a follow-up.")}
				</Alert>
				{downloadError !== null ? (
					<Alert variant="light" color="red" data-testid="image-model-download-error">
						{downloadError}
					</Alert>
				) : null}
				{isDownloadPending ? (
					<Stack gap={4} data-testid="image-model-download-progress">
						<Progress
							value={downloadPercent ?? 100}
							striped={true}
							animated={true}
							size="sm"
							data-testid="image-model-download-bar"
						/>
						<Text size="xs" c="dimmed">
							{downloadPercent === null
								? t(
										"pages.images.models.download.inFlight",
										"Download runs in the background; the model appears when ready. Large files can take several minutes.",
									)
								: t("pages.images.models.download.progress", "Downloading… {{percent}}%", { percent: downloadPercent })}
						</Text>
					</Stack>
				) : null}
				<Group justify="flex-end">
					<Button
						leftSection={<IconDownload size={16} />}
						loading={downloadMutation.isPending || isDownloadPending}
						disabled={!canSubmit || isDownloadPending}
						onClick={handleDownload}
						data-testid="image-model-download-submit"
					>
						{t("pages.images.models.download.submit", "Download")}
					</Button>
				</Group>
			</Stack>
		</Stack>
	);
}
