import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { useRecommendedEmbeddingDownload } from "@/features/knowledge/queries/useRecommendedEmbeddingDownload";

// Shown at the top of the page while this node resolves no embedding model. Without one the server accepts an upload
// and only fails it minutes later in the background embedder, so the precondition is stated up front and the dropzone
// is disabled alongside. The alert clears itself once the download lands: the document-list query reports the
// resolution and polls while the gate is closed, and the page then stops rendering this. The button therefore only
// has to survive the START request, not the whole download.
// The alert body is a flex item, so without min-width 0 it grew to the download button's one-line width and the
// message ran past the card edge on a phone; the button label wraps instead.
const alertStyles = { body: { minWidth: 0 } };
const wrappingButtonStyles = {
	root: { height: "auto", minHeight: "var(--button-height)", maxWidth: "100%" },
	label: { whiteSpace: "normal" as const, paddingBlock: 4 },
};

export function KnowledgeEmbeddingModelAlert() {
	const { t } = useTranslation();
	const download = useRecommendedEmbeddingDownload();

	return (
		<Alert
			color="orange"
			variant="light"
			icon={<IconAlertTriangle size={18} />}
			title={t("pages.knowledgeBase.embeddingMissing.title", "No embedding model installed")}
			styles={alertStyles}
			data-testid="knowledge-embedding-missing-alert"
		>
			<Stack gap="sm" align="flex-start">
				<Text size="sm" style={{ overflowWrap: "anywhere" }}>
					{t(
						"pages.knowledgeBase.embeddingMissing.body",
						"Documents can be uploaded only once an embedding model is installed on this node: without one they cannot be indexed or searched.",
					)}
				</Text>
				<Group gap="sm">
					<Button
						variant="light"
						size="xs"
						styles={wrappingButtonStyles}
						leftSection={<IconCloudDownload size={14} />}
						onClick={() => download.mutate({})}
						loading={download.isPending}
						disabled={download.isPending}
						data-testid="knowledge-embedding-download-recommended"
					>
						{t("pages.nodeSettings.fields.embeddingModel.downloadRecommended", "Download recommended embedding model")}
					</Button>
					<ButtonLink
						to="/node-settings"
						search={{ section: "knowledge" }}
						variant="subtle"
						size="xs"
						data-testid="knowledge-embedding-open-node-settings"
					>
						{t("pages.knowledgeBase.embeddingMissing.openNodeSettings", "Open node settings")}
					</ButtonLink>
				</Group>
			</Stack>
		</Alert>
	);
}
