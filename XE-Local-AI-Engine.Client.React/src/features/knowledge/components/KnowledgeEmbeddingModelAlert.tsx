import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { useRecommendedEmbeddingDownload } from "@/features/knowledge/queries/useRecommendedEmbeddingDownload";

// Shown above the upload panel while this node resolves no embedding model. Without one the server accepts an upload
// and only fails it minutes later in the background embedder, so the precondition is stated up front and the dropzone
// is disabled alongside. The alert clears itself once the download lands: the document-list query reports the
// resolution and polls while the gate is closed, and the page then stops rendering this. The button therefore only
// has to survive the START request, not the whole download.
export function KnowledgeEmbeddingModelAlert() {
	const { t } = useTranslation();
	const download = useRecommendedEmbeddingDownload();

	return (
		<Alert
			color="orange"
			variant="light"
			icon={<IconAlertTriangle size={18} />}
			title={t("pages.knowledgeBase.embeddingMissing.title", "No embedding model installed")}
			data-testid="knowledge-embedding-missing-alert"
		>
			<Stack gap="sm" align="flex-start">
				<Text size="sm">
					{t(
						"pages.knowledgeBase.embeddingMissing.body",
						"Documents can be uploaded only once an embedding model is installed on this node: without one they cannot be indexed or searched.",
					)}
				</Text>
				<Group gap="sm">
					<Button
						variant="light"
						size="xs"
						leftSection={<IconCloudDownload size={14} />}
						onClick={() => download.mutate({})}
						loading={download.isPending}
						disabled={download.isPending}
						data-testid="knowledge-embedding-download-recommended"
					>
						{t("pages.nodeSettings.fields.embeddingModel.downloadRecommended", "Download recommended embedding model")}
					</Button>
					<Button
						component={Link}
						to="/node-settings"
						variant="subtle"
						size="xs"
						data-testid="knowledge-embedding-open-node-settings"
					>
						{t("pages.knowledgeBase.embeddingMissing.openNodeSettings", "Open node settings")}
					</Button>
				</Group>
			</Stack>
		</Alert>
	);
}
