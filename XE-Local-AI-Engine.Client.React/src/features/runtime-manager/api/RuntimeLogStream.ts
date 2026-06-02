import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

/* eslint-disable react-doctor/async-await-in-loop */

// SURVIVOR (hey-api single-source-of-truth migration §7.5): the generated SDK is REST request/response only, so the
// runtime log STREAM stays hand-wrapped. Status (`getRuntimeManagerStatus`) and the container action
// (`executeRuntimeContainerAction`) migrated to the generated SDK; this SignalR streaming bridge does not.

export interface RuntimeLogsRequestDto {
	containerName: string;
	tailLines?: number;
	follow?: boolean;
}

export interface RuntimeLogLineDto {
	containerName: string;
	stream: string;
	line: string;
	observedAt: string;
}

function signalRStream<T>(hubPath: string, methodName: string, request: unknown, signal: AbortSignal): AsyncIterable<T> {
	return {
		async *[Symbol.asyncIterator](): AsyncIterator<T> {
			const connection = new HubConnectionBuilder()
				.withUrl(buildLocalApiUrl(hubPath), {
					accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
				})
				.configureLogging(LogLevel.Warning)
				.build();
			const values: T[] = [];
			let completed = false;
			let failure: unknown;
			let wake: (() => void) | undefined;

			const notify = (): void => {
				wake?.();
				wake = undefined;
			};

			await connection.start();
			const subscription = connection.stream<T>(methodName, request).subscribe({
				next: (value) => {
					values.push(value);
					notify();
				},
				error: (error) => {
					failure = error;
					completed = true;
					notify();
				},
				complete: () => {
					completed = true;
					notify();
				},
			});

			const abort = (): void => {
				subscription.dispose();
				completed = true;
				notify();
			};

			signal.addEventListener("abort", abort, { once: true });

			try {
				while (!completed || values.length > 0) {
					const value = values.shift();
					if (value) {
						yield value;
						continue;
					}

					if (failure) {
						throw failure;
					}

					// biome-ignore lint/performance/noAwaitInLoops: AsyncIterable bridge waits for the next SignalR push before yielding again.
					await new Promise<void>((resolve) => {
						wake = resolve;
					});
				}

				if (failure) {
					throw failure;
				}
			} finally {
				signal.removeEventListener("abort", abort);
				subscription.dispose();
				await connection.stop();
			}
		},
	};
}

export function streamRuntimeLogs(request: RuntimeLogsRequestDto, signal: AbortSignal): AsyncIterable<RuntimeLogLineDto> {
	return signalRStream<RuntimeLogLineDto>("runtime/hub", "StreamLogs", request, signal);
}
