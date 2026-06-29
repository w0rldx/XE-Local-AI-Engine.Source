// Global error collectors: capture-phase `error`, `unhandledrejection`, and `ReportingObserver`.
// All thrown-error paths route through the dedup gate via `recordError` (plan §7.2).

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { describeError, recordError } from "@/core/diagnostics/RecordError";

interface ReportingObserverEntry {
	readonly type?: string;
	readonly body?: { readonly message?: string } | null;
}

interface ReportingObserverLike {
	observe: () => void;
}

type ReportingObserverCtor = new (
	callback: (reports: readonly ReportingObserverEntry[]) => void,
	options?: { buffered?: boolean },
) => ReportingObserverLike;

/** Install `window` error collectors. Returns a teardown that removes every listener. */
export function installWindowErrorCollectors(): () => void {
	const onError = (event: ErrorEvent): void => {
		const described = event.error !== undefined ? describeError(event.error) : { message: event.message };
		recordError({ ...described, source: "uncaught" });
	};

	const onRejection = (event: PromiseRejectionEvent): void => {
		recordError({ ...describeError(event.reason), source: "unhandledrejection" });
	};

	// Capture phase so we observe the error before app-level handlers can stop propagation.
	globalThis.addEventListener("error", onError, true);
	globalThis.addEventListener("unhandledrejection", onRejection);

	const teardownReporting = installReportingObserver();

	return () => {
		globalThis.removeEventListener("error", onError, true);
		globalThis.removeEventListener("unhandledrejection", onRejection);
		teardownReporting();
	};
}

function installReportingObserver(): () => void {
	// biome-ignore lint/style/useNamingConvention: `ReportingObserver` is a fixed-name DOM global, not our property.
	const ctor = (globalThis as { ReportingObserver?: ReportingObserverCtor }).ReportingObserver;
	if (!ctor) {
		return () => undefined;
	}

	let observer: ReportingObserverLike | undefined;
	try {
		observer = new ctor(
			(reports) => {
				for (const report of reports) {
					// ReportingObserver surfaces deprecation/intervention/crash/CSP — not thrown JS — so these
					// are informational lifecycle crumbs, not dedup'd error crumbs.
					push({
						category: "lifecycle",
						message: report.body?.message ?? report.type ?? "report",
						data: { reportType: report.type ?? "unknown" },
					});
				}
			},
			{ buffered: true },
		);
		observer.observe();
	} catch {
		return () => undefined;
	}

	return () => undefined;
}
