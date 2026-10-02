import { Group, Image, Loader, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { useImageObjectUrl } from "@/features/images/hooks/useImageObjectUrl";
import { editModeLabel, type ImageJobView } from "@/features/images/models/ImageModels";

function SourceRemoved() {
	const { t } = useTranslation();
	return (
		<Text size="xs" c="dimmed" fs="italic" data-testid="image-source-removed">
			{t("pages.images.edit.sourceRemoved", "source removed")}
		</Text>
	);
}

interface ImageSourceThumbnailProps {
	imageId: string | null;
	/** The line beside the thumbnail ("Edited from", "Editing from"). */
	label: string;
	"data-testid"?: string;
}

/**
 * A small thumbnail of the image an edit started from, with a label. The source can be deleted after the edit ran —
 * the node then nulls the job's `sourceImageId`, or a cached id answers 404 — and that is an expected state, not a
 * failure: it renders as a plain "source removed" line and raises nothing.
 */
export function ImageSourceThumbnail({ imageId, label, "data-testid": testId }: ImageSourceThumbnailProps) {
	const { url, isLoading, isError } = useImageObjectUrl(imageId);

	return (
		<Group gap="xs" align="center" wrap="nowrap" data-testid={testId}>
			<Text size="xs" c="dimmed">
				{label}
			</Text>
			{isLoading ? <Loader size="xs" /> : null}
			{url ? <Image src={url} alt={label} w={48} h={48} radius="sm" fit="cover" data-testid="image-source-thumbnail" /> : null}
			{imageId === null || isError ? <SourceRemoved /> : null}
		</Group>
	);
}

/** How a job edited its source ("Variation · strength 0.75") and the source it came from; nothing for text-to-image. */
export function ImageEditLineage({ job }: { job: ImageJobView }) {
	const { t } = useTranslation();
	if (job.editMode === null && job.sourceImageId === null) {
		return null;
	}

	const mode = job.editMode === null ? null : editModeLabel(t, job.editMode);
	const detail =
		mode !== null && job.editMode === "img2img" && job.strength !== null
			? t("pages.images.edit.detailWithStrength", "{{mode}} · strength {{strength}}", { mode, strength: job.strength })
			: mode;

	return (
		<Stack gap={4} data-testid="image-edit-lineage">
			{detail === null ? null : (
				<Text size="xs" c="dimmed" data-testid="image-edit-detail">
					{detail}
				</Text>
			)}
			<ImageSourceThumbnail imageId={job.sourceImageId} label={t("pages.images.edit.editedFrom", "Edited from")} />
		</Stack>
	);
}
