import { QueryClient } from "@tanstack/react-query";

import { queryRetryDelay, shouldRetryQuery } from "@/core/api/errors/RetryClassification";
import {
	createDiagnosticsMutationCache,
	createDiagnosticsQueryCache,
} from "@/core/diagnostics/collectors/Query";

const queryClient = new QueryClient({
	// Diagnostics caches record a redacted error breadcrumb after retries exhaust.
	queryCache: createDiagnosticsQueryCache(),
	mutationCache: createDiagnosticsMutationCache(),
	defaultOptions: {
		queries: {
			// Retry ONLY transient failures (transport interruptions, 408/429/5xx). Deterministic 4xx and
			// unclassifiable errors settle immediately — see shouldRetryQuery. The old predicate read the
			// Axios `response.status` shape, which the interceptors have already normalized away for every
			// status except 401/429, so deterministic 400/403/404/500 looked statusless and were retried 3×.
			retry: shouldRetryQuery,
			retryDelay: queryRetryDelay,
		},
	},
});

export function getContext() {
	return {
		queryClient,
	};
}

export { queryClient };
