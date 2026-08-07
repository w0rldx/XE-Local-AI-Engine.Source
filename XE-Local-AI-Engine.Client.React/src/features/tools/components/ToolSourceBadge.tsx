import { Badge } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { ToolCatalogSource } from "@/features/tools/models/ToolCatalogModels";

interface ToolSourceBadgeProps {
	source: ToolCatalogSource;
}

// Small badge that labels where a catalog tool comes from: a node built-in, or a specific MCP server (by its
// slug). Shared by the tool pickers so built-in vs MCP origin is shown consistently. Pure presentation —
// degrades to a generic "MCP" label when the source carries no server slug.
export function ToolSourceBadge({ source }: ToolSourceBadgeProps) {
	const { t } = useTranslation();

	if (source.kind === "builtin") {
		return (
			<Badge size="xs" variant="light" color="gray" data-testid="tool-source-badge-builtin">
				{t("components.toolSourceBadge.builtin", "built-in")}
			</Badge>
		);
	}

	// A user-defined custom tool runs a host command or an outbound fetch, so it is badged in the danger color (filled,
	// not light) to keep its elevated risk visible everywhere the pickers list it.
	if (source.kind === "custom") {
		return (
			<Badge size="xs" variant="filled" color="red" data-testid="tool-source-badge-custom">
				{t("components.toolSourceBadge.custom", "custom")}
			</Badge>
		);
	}

	const label = source.serverSlug
		? t("components.toolSourceBadge.mcpServer", "MCP · {{server}}", { server: source.serverSlug })
		: t("components.toolSourceBadge.mcp", "MCP");

	return (
		<Badge size="xs" variant="light" color="grape" data-testid="tool-source-badge-mcp">
			{label}
		</Badge>
	);
}
