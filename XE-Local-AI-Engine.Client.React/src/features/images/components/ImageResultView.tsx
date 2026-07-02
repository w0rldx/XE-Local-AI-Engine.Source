import { Box, Image, Loader, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { useImageObjectUrl } from "@/features/images/hooks/useImageObjectUrl";

interface ImageResultViewProps {
	imageId: string;
	alt: string;
}

// Renders the decrypted PNG for a succeeded job. The bytes are fetched as an authed blob (bearer via the shared axios
// instance) and object-URL'd (useImageObjectUrl) because the Operator-gated retrieve endpoint can't be reached by a
// plain <img src> (no auth header) and the generated op models the body as 204/void.
export function ImageResultView({ imageId, alt }: ImageResultViewProps) {
	const { t } = useTranslation();
	const { url, isLoading, isError } = useImageObjectUrl(imageId);

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
			<Image src={url} alt={alt} radius="sm" fit="contain" />
		</Box>
	);
}
