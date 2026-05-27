import { createFileRoute } from "@tanstack/react-router";

import { ToolsPage } from "@/features/tools/pages/ToolsPage";

export const Route = createFileRoute("/_layout/tools")({
	component: ToolsPage,
});
