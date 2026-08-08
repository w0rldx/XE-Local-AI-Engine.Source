import { Box, Image, Loader, Text, UnstyledButton } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { ImageViewerDialog } from "@/features/images/components/ImageViewerDialog";
import { useImageObjectUrl } from "@/features/images/hooks/useImageObjectUrl";
import type { ImageJobView } from "@/features/images/models/ImageModels";

interface ImageResultViewProps {
	job: ImageJobView;
	imageId: string;
}

// Renders the decrypted PNG for a succeeded job. The bytes are fetched as an authed blob (bearer via the shared axios
// instance) and object-URL'd (useImageObjectUrl) because the Operator-gated retrieve endpoint can't be reached by a
// plain <img src> (no auth header) and the generated op models the body as 204/void.
//
// The thumbnail is a real button, not an <img onClick>: a bare image is not focusable and ignores Enter/Space, so
// opening the full-size viewer would be mouse-only.
export function ImageResultView({ job, imageId }: ImageResultViewProps) {
	const { t } = useTranslation();
	const { url, isLoading, isError } = useImageObjectUrl(imageId);
	const [isViewerOpen, setIsViewerOpen] = useState(false);

	if (isLoading) {
		return <Loader size="sm" data-testid="image-result-loading" />;
	}

	if (isError || !url) {
		return (
			<Text size="sm" c="red" data-testid="image-result-error">
				{t("pages.images.result.error", "Could not load the generated image.")}
			</Text>
		);
	}

	return (
		<Box maw={320} data-testid="image-result">
			<UnstyledButton
				onClick={() => setIsViewerOpen(true)}
				aria-label={t("pages.images.result.viewFullSize", "View full size")}
				data-testid="image-result-open-viewer"
				style={{ display: "block", width: "100%", cursor: "zoom-in" }}
			>
				<Image src={url} alt={job.prompt} radius="sm" fit="contain" />
			</UnstyledButton>
			<ImageViewerDialog job={job} opened={isViewerOpen} onClose={() => setIsViewerOpen(false)} />
		</Box>
	);
}
