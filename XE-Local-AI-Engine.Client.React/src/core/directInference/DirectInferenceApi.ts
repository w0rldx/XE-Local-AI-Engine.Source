import { HubConnectionState } from "@microsoft/signalr";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	applyDirectInferenceEvent,
	type DirectInferenceEvent,
	type DirectInferenceRequest,
	type DirectInferenceResult,
	emptyDirectInferenceResult,
} from "@/core/directInference/DirectInferenceModels";

const chatHubPath = "chat/hub";

/**
 * Streams one Direct Inference request over the chat hub. Resolves when the stream ends (its last event is `done` or
 * `error`) or the signal aborts; rejects when the hub refuses the request or drops. An abort before the stream starts
 * (while the hub connects) sends nothing; an abort after it cancels the server-side request.
 */
export async function streamDirectInference(
	request: DirectInferenceRequest,
	onEvent: (event: DirectInferenceEvent) => void,
	signal: AbortSignal,
): Promise<void> {
	if (signal.aborted) {
		return;
	}
	const hub = acquireHubConnection(chatHubPath);
	let onAbort: (() => void) | undefined;
	try {
		await hub.whenStarted;
		if (signal.aborted) {
			return;
		}
		if (hub.connection.state !== HubConnectionState.Connected) {
			throw new Error("the chat hub is not connected");
		}
		await new Promise<void>((resolve, reject) => {
			const subscription = hub.connection.stream<DirectInferenceEvent>("StreamDirectInference", request).subscribe({
				next: onEvent,
				error: reject,
				complete: resolve,
			});
			onAbort = (): void => {
				subscription.dispose();
				resolve();
			};
			// The stream starts synchronously above, so nothing can abort between the check after the connect and here.
			signal.addEventListener("abort", onAbort, { once: true });
		});
	} finally {
		// A caller can reuse the signal for its next run: a finished stream must not answer that run's abort.
		if (onAbort) {
			signal.removeEventListener("abort", onAbort);
		}
		hub.release();
	}
}

/** Streams one request, reporting the result folded so far after every event; a hub failure becomes its error message unless the signal aborted. */
export async function runDirectInference(
	request: DirectInferenceRequest,
	onResult: (result: DirectInferenceResult) => void,
	signal: AbortSignal,
): Promise<void> {
	let accumulated = emptyDirectInferenceResult;
	onResult(accumulated);
	try {
		await streamDirectInference(
			request,
			(event) => {
				accumulated = applyDirectInferenceEvent(accumulated, event);
				onResult(accumulated);
			},
			signal,
		);
	} catch (error) {
		if (!signal.aborted) {
			onResult({ ...accumulated, errorMessage: error instanceof Error ? error.message : String(error) });
		}
	}
}
