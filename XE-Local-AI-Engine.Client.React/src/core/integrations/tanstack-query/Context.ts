import { QueryClient } from "@tanstack/react-query";

import {
	createDiagnosticsMutationCache,
	createDiagnosticsQueryCache,
} from "@/core/diagnostics/collectors/Query";

const queryClient = new QueryClient({
	// Diagnostics caches record a redacted error breadcrumb after retries exhaust (plan §7.2).
	queryCache: createDiagnosticsQueryCache(),
	mutationCache: createDiagnosticsMutationCache(),
	defaultOptions: {
		queries: {
			retry: (failureCount, error) => {
				const status = (error as { response?: { status?: number } }).response?.status;
				return status !== 401 && failureCount < 3;
			},
		},
	},
});

export function getContext() {
	return {
		queryClient,
	};
}

export { queryClient };
