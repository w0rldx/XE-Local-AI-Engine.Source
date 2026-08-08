import { Alert, Button, Group, Select, Stack, Switch, Text, TextInput } from "@mantine/core";
import { IconDownload, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { PartRow } from "@/features/images/components/ImageDownloadPartRow";
import {
	type DownloadDraft,
	type ImageModelFamily,
	imageModelFamilies,
	type PartDraft,
} from "@/features/images/models/ImageModels";

const families = imageModelFamilies;

export interface ManualDownloadFormProps {
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
export function ManualDownloadForm({
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
