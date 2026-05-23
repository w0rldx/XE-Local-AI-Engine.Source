import { createFileRoute } from "@tanstack/react-router";

import { PlaceholderPage } from "@/core/ui/pages/PlaceholderPage/PlaceholderPage";

export const Route = createFileRoute("/_layout/node-binding")({
	component: () => (
		<PlaceholderPage
			titleKey="pages.nodeBinding.placeholder.title"
			titleFallback="Node binding"
			descriptionKey="pages.nodeBinding.placeholder.description"
			descriptionFallback="Pairing and binding workflows will be ported from the current node UI."
		/>
	),
});
