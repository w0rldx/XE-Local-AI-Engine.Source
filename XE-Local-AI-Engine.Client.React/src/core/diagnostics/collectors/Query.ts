// TanStack Query collector. Provides `QueryCache`/`MutationCache` with `onError`
// handlers for the QueryClient in Context.ts. The existing `defaultOptions.queries.retry` fn
// (RC2 401-exclude, max 3) is PRESERVED — these caches are added alongside it, not in place of it.

import { MutationCache, QueryCache } from "@tanstack/react-query";

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { describeError } from "@/core/diagnostics/RecordError";

/** A QueryCache that records a redacted error breadcrumb after retries are exhausted. */
export function createDiagnosticsQueryCache(): QueryCache {
	return new QueryCache({
		onError: (error, query) => {
			recordQueryError("query", error, stringifyKey(query.queryKey));
		},
	});
}

/** A MutationCache that records a redacted error breadcrumb on mutation failure. */
export function createDiagnosticsMutationCache(): MutationCache {
	return new MutationCache({
		onError: (error, _variables, _context, mutation) => {
			recordQueryError("mutation", error, stringifyKey(mutation.options.mutationKey));
		},
	});
}

function recordQueryError(kind: "query" | "mutation", error: unknown, key: string): void {
	const described = describeError(error);
	push({
		category: "error",
		error: {
			message: `[${kind}] ${key}: ${described.message}`,
			source: "uncaught",
			...(described.stack === undefined ? {} : { stack: described.stack }),
		},
	});
}

function stringifyKey(key: unknown): string {
	if (key === undefined) {
		return "(no key)";
	}
	try {
		return JSON.stringify(key) ?? String(key);
	} catch {
		return String(key);
	}
}
