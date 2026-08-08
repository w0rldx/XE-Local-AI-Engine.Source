// TanStack Router collector: subscribe to resolved navigations → navigation breadcrumbs.

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { router } from "@/core/integrations/tanstack-router/Router";

function currentHref(): string {
	return router.state.location.href;
}

/** Subscribe to router navigations. Returns the unsubscribe handle. */
export function installRouterCollector(): () => void {
	let previous = currentHref();

	// The event payload is ignored; we read the resolved location from router state.
	const unsubscribe = router.subscribe("onResolved", () => {
		const next = currentHref();
		if (next !== previous) {
			push({ category: "navigation", from: previous, to: next });
			previous = next;
		}
	});

	return unsubscribe;
}
