// Fixed-size redacted ring buffer — the single source every collector appends to.
//
// Always-on collectors do O(1) appends here; the snapshot bundler reads the buffer on capture.
// Redaction runs on every `push` (defensive, idempotent) so secrets never enter the ring.

import { generateId } from "@/core/diagnostics/Ids";
import { redactBreadcrumb } from "@/core/diagnostics/Redact";
import type { Breadcrumb, BreadcrumbInput } from "@/core/diagnostics/Types";

/** Max breadcrumbs retained. */
export const BREADCRUMB_BUFFER_CAPACITY = 200;

const buffer: Breadcrumb[] = [];

/** Append a breadcrumb. The buffer stamps `id`/`timestamp`, redacts, and evicts the oldest at cap. */
export function push(input: BreadcrumbInput): Breadcrumb {
	const crumb = redactBreadcrumb({
		...input,
		id: generateId(),
		timestamp: Date.now(),
	} as Breadcrumb);

	buffer.push(crumb);
	if (buffer.length > BREADCRUMB_BUFFER_CAPACITY) {
		buffer.splice(0, buffer.length - BREADCRUMB_BUFFER_CAPACITY);
	}
	return crumb;
}

/** Snapshot of the current breadcrumbs, oldest-first. Returns a copy (callers cannot mutate the ring). */
export function getAll(): readonly Breadcrumb[] {
	return [...buffer];
}

/** Drop every breadcrumb (used on snapshot capture / panel "Clear"). */
export function clear(): void {
	buffer.length = 0;
}
