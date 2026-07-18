import { Badge, Tooltip } from "@mantine/core";
import { IconShieldHalf } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ToolCategory } from "@/features/tools/models/ToolCatalogModels";

interface ToolCategoryBadgeProps {
	category: ToolCategory;
	// Whether the CURRENT node policy gates this tool behind an approval round-trip (the agent-independent floor). When
	// true the badge carries a shield and the tooltip explains WHY it is gated; when false it reads as auto-executing.
	effectiveRequiresApproval: boolean;
}

// Per-category Mantine badge color. ReadLocal is the low-risk (auto) tone; the write/execute, network, orchestration
// and fail-closed Unknown classes use warmer, higher-attention tones.
const CATEGORY_COLOR: Record<ToolCategory, string> = {
	ReadLocal: "teal",
	WriteExecute: "red",
	Orchestration: "violet",
	Network: "orange",
	Unknown: "red",
};

// Small badge that labels a tool's risk class (OPP-03) and surfaces whether the node policy gates it behind approval.
// Shared by the agent tool selector and the chat tool card so a tool's class reads consistently wherever it appears.
// Pure presentation; the WHY of the gating lives in the tooltip. An unrecognized category is parsed upstream to
// "Unknown" (fail-closed), so this component only ever renders a known class.
export function ToolCategoryBadge({ category, effectiveRequiresApproval }: ToolCategoryBadgeProps) {
	const { t } = useTranslation();

	const label = t(`components.toolCategoryBadge.label.${category}`, defaultCategoryLabel(category));
	const meaning = t(`components.toolCategoryBadge.meaning.${category}`, defaultCategoryMeaning(category));
	const approvalNote = effectiveRequiresApproval
		? category === "Unknown"
			? t("components.toolCategoryBadge.approval.always", "Uncategorized tools always require approval (fail-closed).")
			: t("components.toolCategoryBadge.approval.byPolicy", "Requires approval under the current node policy.")
		: t("components.toolCategoryBadge.approval.auto", "Auto-executes under the current node policy.");

	return (
		<Tooltip label={`${meaning} ${approvalNote}`} multiline={true} w={260} withArrow={true}>
			<Badge
				size="xs"
				variant="light"
				color={CATEGORY_COLOR[category]}
				leftSection={effectiveRequiresApproval ? <IconShieldHalf size={11} /> : undefined}
				data-testid={`tool-category-badge-${category}`}
				data-requires-approval={effectiveRequiresApproval}
			>
				{label}
			</Badge>
		</Tooltip>
	);
}

// Fallback labels/meanings kept alongside the i18n keys so the component renders sensibly even before a translation
// bundle loads (mirrors how the other tool badges inline English defaults).
function defaultCategoryLabel(category: ToolCategory): string {
	switch (category) {
		case "ReadLocal":
			return "read-only";
		case "WriteExecute":
			return "write/execute";
		case "Orchestration":
			return "orchestration";
		case "Network":
			return "network";
		default:
			return "uncategorized";
	}
}

function defaultCategoryMeaning(category: ToolCategory): string {
	switch (category) {
		case "ReadLocal":
			return "Read-only, node-local tools with no side effects.";
		case "WriteExecute":
			return "Can write files or run commands on the node.";
		case "Orchestration":
			return "Can spawn or drive other agents or models.";
		case "Network":
			return "Reaches an external service outside the node.";
		default:
			return "Uncategorized tool of unknown risk class.";
	}
}
