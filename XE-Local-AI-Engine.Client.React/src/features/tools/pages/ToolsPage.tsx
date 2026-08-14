import { IconTool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { LocalToolsOverview } from "@/features/chat/components/LocalToolsOverview";

// The catalog rendered by LocalToolsOverview is now dynamic (dynamic tool-catalog): it fetches built-in tools plus the
// tools discovered from enabled MCP servers via useToolCatalog. MCP server registration is managed on the
// dedicated /mcp page; this page is the read-only catalog view.

export function ToolsPage() {
	const { t } = useTranslation();

	return (
		<PageShell data-testid="tools-page">
			<PageHeader
				title={t("pages.tools.title", "Local tools")}
				icon={<IconTool size={24} />}
				subtitle={t(
					"pages.tools.subtitle",
					"Tools available to the local node agent: built-in in-process tools plus tools from enabled MCP servers.",
				)}
			/>
			<LocalToolsOverview />
		</PageShell>
	);
}
