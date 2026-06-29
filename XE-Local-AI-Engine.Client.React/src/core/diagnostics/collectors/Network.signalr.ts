// SignalR network collector (plan §7.2). Helper wired into NodeChatConnection's `.withUrl(...)`.
//
// SignalR bypasses axios entirely, so it carries its own FE-generated `traceparent` (as a hub
// header) and records its own `{transport:'signalr'}` breadcrumb. The header merges with the
// existing `accessTokenFactory` + autoReconnect options — it does not replace them.

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { toNetworkEntry } from "@/core/diagnostics/Redact";
import { generateTraceparent } from "@/core/diagnostics/Trace";

/**
 * Build the diagnostics headers for a SignalR hub connection and record a `signalr` network
 * breadcrumb for the connect. Spread the result into `withUrl(url, { headers: ... })`.
 */
export function diagnosticsSignalrHeaders(url: string): Record<string, string> {
	const { header, traceId } = generateTraceparent();
	push({
		category: "network",
		entry: toNetworkEntry({ transport: "signalr", method: "CONNECT", url, traceId }),
	});
	return { traceparent: header };
}

/** Record a SignalR hub error breadcrumb (e.g. from `onclose`). */
export function recordSignalrError(url: string, status?: number): void {
	push({
		category: "network",
		entry: toNetworkEntry({
			transport: "signalr",
			method: "HUB",
			url,
			...(status === undefined ? {} : { status }),
		}),
	});
}
