import { Alert, Badge, Button, Card, Group, Loader, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconDownload } from "@tabler/icons-react";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import { toast } from "@/core/ui/notifications/Toast";
import type { ImageModelView } from "@/features/images/models/ImageModels";
import { useStartImageModelDownload } from "@/features/images/queries/useImageQueries";

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
}

// Minimal image-model management (plan §8: keep minimal — full download-progress UI is a follow-up). Lists installed
// image models and offers a detached "download model" action (202 accepted) that starts a single-file (Diffusion-part)
// weight download; the installed list is polled via query invalidation, so the model appears once the download lands.
export function ImageModelManager({ models, isLoading }: ImageModelManagerProps) {
	const { t } = useTranslation();
	const [draft, setDraft] = useState<DownloadDraft>(emptyDraft);
	const downloadMutation = useStartImageModelDownload();

	const canSubmit = draft.repoId.trim().length > 0 && draft.fileName.trim().length > 0 && draft.modelName.trim().length > 0;

	const handleDownload = useCallback(() => {
		if (!canSubmit) {
			return;
		}
		downloadMutation.mutate(
			{
				modelName: draft.modelName.trim(),
				repoId: draft.repoId.trim(),
				family: draft.family,
				parts: [{ role: "Diffusion", fileName: draft.fileName.trim() }],
			},
			{
				onSuccess: () => {
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
				<Group justify="flex-end">
					<Button
						leftSection={<IconDownload size={16} />}
						loading={downloadMutation.isPending}
						disabled={!canSubmit}
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
