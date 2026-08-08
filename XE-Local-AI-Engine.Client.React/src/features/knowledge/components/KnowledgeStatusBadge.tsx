import { Badge, Loader } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { type KnowledgeDocumentStatus, knowledgeStatusDescriptor } from "@/features/knowledge/models/KnowledgeModels";

interface KnowledgeStatusBadgeProps {
	readonly status: KnowledgeDocumentStatus;
}

// Colored status pill for a knowledge document. Terminal states are sharp accents (Indexed green, Failed red);
// in-progress pipeline states share an amber pill with an inline spinner so the operator sees work is happening.
export function KnowledgeStatusBadge({ status }: KnowledgeStatusBadgeProps) {
	const { t } = useTranslation();
	const descriptor = knowledgeStatusDescriptor(status);

	return (
		<Badge
			color={descriptor.color}
			variant="light"
			leftSection={descriptor.inProgress ? <Loader size={10} color={descriptor.color} /> : undefined}
			data-testid={`knowledge-status-${status}`}
		>
			{t(descriptor.labelKey)}
		</Badge>
	);
}
