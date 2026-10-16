import { Box } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { type KnowledgeDocumentStatus, knowledgeStatusDescriptor } from "@/features/knowledge/models/KnowledgeModels";

interface KnowledgeStatusBadgeProps {
	readonly status: KnowledgeDocumentStatus;
}

// Colored status pill for a knowledge document. Terminal states are sharp accents (Indexed green, Failed red);
// in-progress pipeline states share an amber pill with an inline spinner so the operator sees work is happening.
export function KnowledgeStatusBadge({ status }: KnowledgeStatusBadgeProps) {
	const { t } = useTranslation();
	const descriptor = knowledgeStatusDescriptor(status);

	// The badge clips with an ellipsis, so a squeezed table column cut it to "INDE…"; a max-content floor keeps the
	// longest English and German label whole.
	return (
		<Box style={{ minWidth: "max-content" }} data-testid={`knowledge-status-box-${status}`}>
			<StatusBadge
				color={descriptor.color}
				label={t(descriptor.labelKey)}
				inProgress={descriptor.inProgress}
				data-testid={`knowledge-status-${status}`}
			/>
		</Box>
	);
}
