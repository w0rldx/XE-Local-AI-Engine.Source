import { Button, FileButton } from "@mantine/core";
import { IconUpload } from "@tabler/icons-react";
import { useRef } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import { useUploadImage } from "@/features/images/queries/useImageQueries";

/**
 * Uploads a PNG or JPEG to edit. The picker filters by type; format, size and dimension checks are the node's, and its
 * 400 carries a fixed sentence ("The image is larger than the upload size limit.") that is shown as is.
 */
export function ImageUploadButton() {
	const { t } = useTranslation();
	const upload = useUploadImage();
	// The input keeps the last file as its value, so picking that same file again (a retry after a refusal, or after
	// deleting it) fires no change event. Clearing it once the upload settles makes every pick a fresh one.
	const resetRef = useRef<() => void>(null);

	const handleFile = (file: File | null): void => {
		if (file === null) {
			return;
		}
		upload.mutate(file, {
			onError: (error) => toast.error(apiErrorMessage(error, t("pages.images.uploads.error", "Could not upload the image."))),
			onSettled: () => resetRef.current?.(),
		});
	};

	return (
		// The Button IS the labelled control; FileButton's own input is display:none and only opens the OS picker.
		<FileButton
			onChange={handleFile}
			resetRef={resetRef}
			accept="image/png,image/jpeg"
			inputProps={{ "aria-hidden": true, tabIndex: -1 }}
		>
			{(props) => (
				<Button
					{...props}
					size="xs"
					variant="default"
					leftSection={<IconUpload size={14} />}
					loading={upload.isPending}
					data-testid="image-upload-button"
				>
					{t("pages.images.uploads.upload", "Upload image to edit")}
				</Button>
			)}
		</FileButton>
	);
}
