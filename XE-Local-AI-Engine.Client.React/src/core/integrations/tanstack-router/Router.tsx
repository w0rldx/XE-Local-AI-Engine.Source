import { createRouter } from "@tanstack/react-router";

import * as TanStackQueryProvider from "@/core/integrations/tanstack-query/Provider";
import { NavigationLoadingIndicator } from "@/core/ui/components/NavigationLoadingIndicator/NavigationLoadingIndicator";

import { routeTree } from "@/routeTree.gen";

export const router = createRouter({
	routeTree,
	basepath: "/app",
	context: {
		...TanStackQueryProvider.getContext(),
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
