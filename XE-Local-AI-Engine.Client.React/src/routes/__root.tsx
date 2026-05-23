import { createRootRouteWithContext, Outlet, useRouter } from "@tanstack/react-router";

import { AppErrorFallback } from "@/AppErrorFallback";
import { NotFound } from "@/core/ui/pages/NotFound/NotFound";
import type { MyRouterContext, RootErrorComponentProps } from "@/core/integrations/tanstack-router/Root.types";

export const Route = createRootRouteWithContext<MyRouterContext>()({
	component: (): React.ReactElement | null => <Outlet />,
	errorComponent: RootErrorComponent,
	notFoundComponent: () => <NotFound />,
});

function RootErrorComponent({ error, reset }: RootErrorComponentProps) {
	const router = useRouter();

	return (
		<AppErrorFallback
			error={error}
			onRetry={() => {
				reset();
				router.invalidate();
			}}
		/>
	);
}
