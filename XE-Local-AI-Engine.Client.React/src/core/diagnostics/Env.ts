// Capture-time environment facts for a snapshot. The snapshot bundler calls `collectEnv()`.

import type { SnapshotEnv } from "@/core/diagnostics/Types";
import { environment } from "@/Environment";

/** Read route/version/UA/viewport/locale from the live browser context. */
export function collectEnv(): SnapshotEnv {
	const location = globalThis.location;
	const navigatorApi = globalThis.navigator;
	return {
		route: location ? `${location.pathname}${location.search}` : "",
		appVersion: environment.VITE_APP_VERSION,
		userAgent: navigatorApi?.userAgent ?? "",
		viewport: {
			width: globalThis.innerWidth ?? 0,
			height: globalThis.innerHeight ?? 0,
		},
		locale: navigatorApi?.language ?? "",
	};
}
