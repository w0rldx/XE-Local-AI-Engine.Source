// Shared dedup'd error-crumb path used by every error collector (window, react, reporting).
// Routing all error sources through here guarantees the plan §3 "one error → one breadcrumb" rule.

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { buildErrorKey, shouldRecord } from "@/core/diagnostics/Dedup";
import type { Breadcrumb, ErrorBreadcrumb, ErrorSource } from "@/core/diagnostics/Types";

export interface RecordErrorInput {
	readonly message: string;
	readonly stack?: string;
	readonly componentStack?: string;
	readonly source: ErrorSource;
}

/**
 * Notified once per recorded (non-deduped) error breadcrumb. Lane B subscribes here to auto-capture
 * an error snapshot; because the listener fires only AFTER the dedup gate passes, a single logical
 * error triggers exactly one capture even when several collectors observe it.
 */
export type ErrorRecordedListener = (crumb: ErrorBreadcrumb) => void;

const errorRecordedListeners = new Set<ErrorRecordedListener>();

/** Subscribe to deduped error recordings. Returns an unsubscribe function. */
export function onErrorRecorded(listener: ErrorRecordedListener): () => void {
	errorRecordedListeners.add(listener);
	return () => {
		errorRecordedListeners.delete(listener);
	};
}

/**
 * Push an error breadcrumb unless the same `(message + top frame)` was recorded within the dedup
 * window. Returns the pushed crumb, or undefined when suppressed as a duplicate.
 */
export function recordError(input: RecordErrorInput): Breadcrumb | undefined {
	if (!shouldRecord(buildErrorKey(input.message, input.stack))) {
		return undefined;
	}

	const crumb = push({
		category: "error",
		error: {
			message: input.message,
			source: input.source,
			...(input.stack === undefined ? {} : { stack: input.stack }),
			...(input.componentStack === undefined ? {} : { componentStack: input.componentStack }),
		},
	});

	if (crumb.category === "error") {
		for (const listener of errorRecordedListeners) {
			listener(crumb);
		}
	}

	return crumb;
}

/** Normalize an unknown thrown value into a message + stack pair. */
export function describeError(value: unknown): { message: string; stack?: string } {
	if (value instanceof Error) {
		return value.stack === undefined ? { message: value.message } : { message: value.message, stack: value.stack };
	}
	if (typeof value === "string") {
		return { message: value };
	}
	try {
		return { message: JSON.stringify(value) ?? String(value) };
	} catch {
		return { message: String(value) };
	}
}
