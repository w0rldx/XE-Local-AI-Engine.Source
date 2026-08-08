import { Badge, Tooltip } from "@mantine/core";
import { IconHistory } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

// Discloses that a knowledge result/document is being served from its previously-indexed projection while a
// re-index is pending or after a re-index failed (serve last-known-good, but never silently). Used both
// on search hits (driven by the wire `servingLastKnownGood` flag) and on document rows (computed from status +
// chunk count). Amber, tooltip-explained, so an operator can tell fresh content from stale-but-usable content.
export function KnowledgeLastKnownGoodBadge() {
	const { t } = useTranslation();

	return (
		<Tooltip
			label={t(
				"pages.knowledgeBase.lastKnownGood.tooltip",
				"This document is being re-indexed or its last re-index failed. Results are served from the previously indexed version and may be out of date.",
			)}
			multiline={true}
			maw={280}
			withArrow={true}
		>
			<Badge
				color="orange"
				variant="light"
				leftSection={<IconHistory size={12} />}
				style={{ flexShrink: 0 }}
				data-testid="knowledge-last-known-good"
			>
				{t("pages.knowledgeBase.lastKnownGood.label", "Last-known-good")}
			</Badge>
		</Tooltip>
	);
}
