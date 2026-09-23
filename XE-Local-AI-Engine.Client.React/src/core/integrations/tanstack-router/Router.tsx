import { createRouter } from "@tanstack/react-router";

import { registerLoginNavigator } from "@/core/api/axios/LoginNavigation";
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

// The axios 401 interceptor redirects through this registration rather than importing the router (LoginNavigation.ts).
registerLoginNavigator((redirect) => router.navigate({ to: "/login", search: { redirect } }));

declare module "@tanstack/react-router" {
	interface Register {
		router: typeof router;
	}
}
