import { createRootRouteWithContext, Outlet } from "@tanstack/react-router";

import type { MyRouterContext } from "@/core/integrations/tanstack-router/Root.types";
import { RootErrorComponent } from "@/core/integrations/tanstack-router/RootErrorComponent";
import { NotFound } from "@/core/ui/pages/NotFound/NotFound";

export const Route = createRootRouteWithContext<MyRouterContext>()({
	component: (): React.ReactElement | null => <Outlet />,
	errorComponent: RootErrorComponent,
	notFoundComponent: () => <NotFound />,
});
