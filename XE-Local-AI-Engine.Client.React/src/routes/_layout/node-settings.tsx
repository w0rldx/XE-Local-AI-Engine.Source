import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/node-settings")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.nodeSettings.placeholder.title"
			titleFallback="Node settings"
			descriptionKey="pages.nodeSettings.placeholder.description"
			descriptionFallback="Local node settings will be backed by the node FastEndpoints API."
		/>
	),
});
