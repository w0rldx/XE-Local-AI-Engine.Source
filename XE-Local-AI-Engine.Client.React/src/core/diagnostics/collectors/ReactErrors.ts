// React 19 error collectors.
//
// `rootErrorHandlers` is spread into `createRoot(rootElement, { ... })` in Main.tsx.
// `onAppError` is passed to the App.tsx ErrorBoundary `onError` prop (NO such prop today — we add it).
// All paths route through the shared dedup'd `recordError`, so an error caught by both the boundary
// and the root is recorded once.

import { describeError, recordError } from "@/core/diagnostics/RecordError";

/** Minimal shape of React's `ErrorInfo` (avoids importing react types into a cross-cutting module). */
interface ReactErrorInfo {
	readonly componentStack?: string | null;
}

/** Handlers for `ReactDOM.createRoot(node, { onUncaughtError, onCaughtError })`. */
export const rootErrorHandlers = {
	onUncaughtError: (error: unknown, errorInfo: ReactErrorInfo): void => {
		recordError({
			...describeError(error),
			source: "uncaught",
			...(errorInfo.componentStack ? { componentStack: errorInfo.componentStack } : {}),
		});
	},
	onCaughtError: (error: unknown, errorInfo: ReactErrorInfo): void => {
		recordError({
			...describeError(error),
			source: "boundary",
			...(errorInfo.componentStack ? { componentStack: errorInfo.componentStack } : {}),
		});
	},
} as const;

/** `onError` handler for the App.tsx `<ErrorBoundary>` (react-error-boundary signature). */
export function onAppError(error: unknown, info: ReactErrorInfo): void {
	recordError({
		...describeError(error),
		source: "boundary",
		...(info.componentStack ? { componentStack: info.componentStack } : {}),
	});
}
