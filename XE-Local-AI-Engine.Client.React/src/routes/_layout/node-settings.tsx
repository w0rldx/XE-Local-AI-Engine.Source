import { createFileRoute } from "@tanstack/react-router";

import { NodeSettings } from "@/features/node-settings/pages/NodeSettings";

export const Route = createFileRoute("/_layout/node-settings")({
	component: NodeSettings,
});
