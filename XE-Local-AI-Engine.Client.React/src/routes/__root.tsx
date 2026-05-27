import { createRootRouteWithContext, Outlet } from "@tanstack/react-router";

import type { MyRouterContext } from "@/core/integrations/tanstack-router/Root.types";
import { NotFound } from "@/core/ui/pages/NotFound/NotFound";
import { RootErrorComponent } from "@/routes/RootErrorComponent";

export const Route = createRootRouteWithContext<MyRouterContext>()({
	component: (): React.ReactElement | null => <Outlet />,
	errorComponent: RootErrorComponent,
	notFoundComponent: () => <NotFound />,
});
