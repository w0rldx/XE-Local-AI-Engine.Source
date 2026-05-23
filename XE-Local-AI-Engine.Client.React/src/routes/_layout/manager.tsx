import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/manager")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.manager.placeholder.title"
			titleFallback="Runtime manager"
			descriptionKey="pages.manager.placeholder.description"
			descriptionFallback="HostAgent runtime status and container actions will be added here."
		/>
	),
});
