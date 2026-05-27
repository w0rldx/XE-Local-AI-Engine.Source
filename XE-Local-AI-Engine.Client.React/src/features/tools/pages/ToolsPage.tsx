import { Box, Stack, Text, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { LocalToolsOverview } from "@/features/chat/components/LocalToolsOverview";

// Extension seam: in a future release MCP tools will populate the same list below
// LocalToolsOverview. For now only the in-process catalog (GetCurrentTime, Calculate) is shown.
// To extend: add a <McpToolsOverview /> section here once the MCP tool provider ships.

export function ToolsPage() {
	const { t } = useTranslation();

	return (
		<Box py="lg" data-testid="tools-page">
			<Stack gap="md">
				<div>
					<Title order={3}>{t("pages.tools.title", "Local tools")}</Title>
					<Text size="sm" c="dimmed">
						{t("pages.tools.subtitle", "In-process tools available to the local node agent. All tools run on this device — no external access.")}
					</Text>
				</div>
				<LocalToolsOverview />
			</Stack>
		</Box>
	);
}
