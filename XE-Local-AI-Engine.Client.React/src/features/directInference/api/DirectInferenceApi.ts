import { HubConnectionState } from "@microsoft/signalr";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import type { DirectInferenceEvent, DirectInferenceRequest } from "@/features/directInference/models/DirectInferenceModels";

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
			const onAbort = (): void => {
				subscription.dispose();
				resolve();
			};
			// The stream starts synchronously above, so nothing can abort between the check after the connect and here.
			signal.addEventListener("abort", onAbort, { once: true });
		});
	} finally {
		hub.release();
	}
}
