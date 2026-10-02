import { Badge, Button, Card, Group, Image, Loader, Stack, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { useImageObjectUrl } from "@/features/images/hooks/useImageObjectUrl";
import type { ImageEditSource, UploadedImageView } from "@/features/images/models/ImageModels";
import { useDeleteUploadedImage } from "@/features/images/queries/useImageQueries";

interface UploadedImageCardProps {
	image: UploadedImageView;
	/** Opens the generation form on this upload; absent when no installed model can edit. */
	onEdit?: (source: ImageEditSource) => void;
}

/**
 * An image the operator uploaded to edit. It has no prompt, seed or model — only its size — so it is its own card
 * rather than a job card with blanks. Delete removes the upload's row and encrypted bytes; jobs already edited from
 * it keep their results and show "source removed" from then on.
 */
export function UploadedImageCard({ image, onEdit }: UploadedImageCardProps) {
	const { t } = useTranslation();
	const { url, isLoading, isError } = useImageObjectUrl(image.imageId);
	const deleteUpload = useDeleteUploadedImage();
	const label = t("pages.images.uploads.uploaded", "Uploaded");

	return (
		<Card withBorder={true} padding="sm" radius="md" data-testid="uploaded-image-card">
			<Stack gap="xs">
				{isLoading ? <Loader size="sm" /> : null}
				{isError ? (
					<Text size="sm" c="red">
						{t("pages.images.uploads.loadError", "Could not load the uploaded image.")}
					</Text>
				) : null}
				{url ? <Image src={url} alt={label} radius="sm" fit="contain" mah={200} /> : null}
				<Group justify="space-between" align="center" wrap="nowrap">
					<Group gap="xs" wrap="nowrap">
						<Badge variant="light">{label}</Badge>
						<Text size="xs" c="dimmed" data-testid="uploaded-image-size">
							{t("pages.images.uploads.size", "{{width}}×{{height}}", { width: image.width, height: image.height })}
						</Text>
					</Group>
					<Group gap={4} wrap="nowrap">
						{onEdit === undefined ? null : (
							<Button
								size="xs"
								variant="subtle"
								leftSection={<IconPencil size={14} />}
								onClick={() => onEdit({ imageId: image.imageId, width: image.width, height: image.height })}
								data-testid="uploaded-image-edit"
							>
								{t("pages.images.edit.action", "Edit")}
							</Button>
						)}
						<Button
							size="xs"
							variant="subtle"
							color="red"
							leftSection={<IconTrash size={14} />}
							loading={deleteUpload.isPending}
							onClick={() =>
								deleteUpload.mutate(image.imageId, {
									onError: (error) =>
										toast.error(
											apiErrorMessage(error, t("pages.images.uploads.deleteError", "Could not delete the uploaded image.")),
										),
								})
							}
							data-testid="uploaded-image-delete"
						>
							{t("common.delete", "Delete")}
						</Button>
					</Group>
				</Group>
			</Stack>
		</Card>
	);
}
