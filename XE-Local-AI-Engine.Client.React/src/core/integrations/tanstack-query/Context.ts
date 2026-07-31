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
			// Without this, staleTime defaults to 0, which makes every mounted query stale the instant it
			// settles — so the default refetchOnWindowFocus/refetchOnMount refetched the entire page on every
			// alt-tab back into the app, each response re-running zod validation and producing fresh object
			// identities that re-rendered (and re-parsed the markdown of) whole conversations.
			//
			// 30s matches the value several hooks already set by hand (useImageQueries, useDevelopment,
			// useGgufDownload, useCodexModelOptions). It does NOT weaken write-path freshness: invalidateQueries
			// marks a query stale explicitly and still refetches immediately, and live surfaces are pushed over
			// SignalR rather than polled.
			staleTime: 30_000,
		},
	},
});

export function getContext() {
	return {
		queryClient,
	};
}

export { queryClient };
