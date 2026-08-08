import { Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconPhoto } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { isRemoteImageSrc, remoteImageOrigin } from "@/features/chat/components/MarkdownImagePolicy";

interface SafeMarkdownImageProps {
	src?: string;
	alt?: string;
}

// react-markdown `img` renderer that enforces the model-markdown image policy (see MarkdownImagePolicy): remote images
// load only after explicit consent, and a consented remote image suppresses its referrer. An empty src (e.g. a
// dangerous scheme that markdownImageUrlTransform stripped to "") renders nothing.
export function SafeMarkdownImage({ src, alt }: SafeMarkdownImageProps) {
	const { t } = useTranslation();
	// Bind consent to the exact source it was granted for. If React reuses this element position and the
	// src prop changes to a new remote origin, the stale consent must not carry over to that new origin.
	const [consentedSrc, setConsentedSrc] = useState<string | null>(null);

	const source = typeof src === "string" ? src : "";
	const altText = typeof alt === "string" ? alt : "";
	if (source.length === 0) {
		return null;
	}

	const remote = isRemoteImageSrc(source);
	const consented = consentedSrc === source;

	if (remote && !consented) {
		const origin = remoteImageOrigin(source);
		return (
			<UnstyledButton
				type="button"
				onClick={() => setConsentedSrc(source)}
				data-testid="remote-image-consent"
				style={{
					display: "inline-flex",
					maxWidth: "100%",
					padding: "var(--mantine-spacing-xs) var(--mantine-spacing-sm)",
					border: "1px dashed var(--mantine-color-default-border)",
					borderRadius: "var(--mantine-radius-sm)",
				}}
			>
				<Group gap="xs" wrap="nowrap" align="center">
					<IconPhoto size={18} />
					<Stack gap={0} style={{ minWidth: 0 }}>
						<Text size="sm">{t("pages.chat.remoteImage.load", "Load remote image")}</Text>
						<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>
							{origin}
						</Text>
					</Stack>
				</Group>
			</UnstyledButton>
		);
	}

	return (
		<img
			src={source}
			alt={altText}
			// A consented remote image must not leak the chat page URL as a referrer to the third-party host.
			referrerPolicy={remote ? "no-referrer" : undefined}
			style={{ maxWidth: "100%", height: "auto" }}
		/>
	);
}
