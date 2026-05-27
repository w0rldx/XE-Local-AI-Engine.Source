import { createRouter } from "@tanstack/react-router";

import { getContext as getTanStackQueryContext } from "@/core/integrations/tanstack-query/Context";
import { NavigationLoadingIndicator } from "@/core/ui/components/NavigationLoadingIndicator/NavigationLoadingIndicator";
import { routeTree } from "@/routeTree.gen";

export const router = createRouter({
	routeTree,
	context: {
		...getTanStackQueryContext(),
	},
	defaultPendingComponent: () => <NavigationLoadingIndicator />,
	scrollRestoration: true,
	defaultStructuralSharing: true,
	defaultPreload: "intent",
	defaultPreloadStaleTime: 0,
});

declare module "@tanstack/react-router" {
	interface Register {
		router: typeof router;
	}
}
