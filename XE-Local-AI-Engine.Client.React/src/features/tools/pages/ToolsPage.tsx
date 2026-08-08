import { Box, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { LocalToolsOverview } from "@/features/chat/components/LocalToolsOverview";

// The catalog rendered by LocalToolsOverview is now dynamic (dynamic tool-catalog): it fetches built-in tools plus the
// tools discovered from enabled MCP servers via useToolCatalog. MCP server registration is managed on the
// dedicated /mcp page; this page is the read-only catalog view.

export function ToolsPage() {
	const { t } = useTranslation();

	return (
		<Box py="lg" data-testid="tools-page">
			<Stack gap="md">
				<div>
					<Title order={3}>{t("pages.tools.title", "Local tools")}</Title>
					<Text size="sm" c="dimmed">
						{t(
							"pages.tools.subtitle",
							"Tools available to the local node agent: built-in in-process tools plus tools from enabled MCP servers.",
						)}
					</Text>
				</div>
				<LocalToolsOverview />
			</Stack>
		</Box>
	);
}
