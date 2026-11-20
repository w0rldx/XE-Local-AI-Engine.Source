import type { QueryClient, QueryKey } from "@tanstack/react-query";

import { isRecord } from "@/core/models/JsonRecord";

/** Builds the partial generated-query-key filter that matches every cached variant of one hey-api endpoint. */
export function operationInvalidationKey(operationId: string): readonly [{ _id: string }] {
	return [{ _id: operationId }];
}

export interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

/** A feed query keyed by one id: disabled until the id exists, polled only while the hub is unavailable. */
export function feedQuerySettings(id: string | undefined, options: FeedOptions) {
	return {
		enabled: (options.enabled ?? true) && Boolean(id),
		refetchInterval: options.pollIntervalMs ?? false,
	} as const;
}

/**
 * The `version` the cache holds now for item `id` of a cached list (a `{ items }` response or a plain array), or
 * `fallback` when the item is not cached. Read after an awaited confirmation, so a row a poll refreshed while the dialog
 * was open is written with its current optimistic-concurrency version instead of the one seen at the click.
 */
export function cachedItemVersion(queryClient: QueryClient, queryKey: QueryKey, id: string, fallback: number): number {
	const data = queryClient.getQueryData<unknown>(queryKey);
	const items: unknown[] = Array.isArray(data) ? data : isRecord(data) && Array.isArray(data["items"]) ? data["items"] : [];
	const item = items.find((candidate) => isRecord(candidate) && candidate["id"] === id);
	return isRecord(item) && typeof item["version"] === "number" ? item["version"] : fallback;
}
