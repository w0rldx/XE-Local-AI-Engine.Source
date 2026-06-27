// Kokoro TTS provider — wraps the dedicated worker that runs `KokoroTTS`.
//
// One provider instance maps to one execution path: WebGPU (`device:'webgpu'`, `dtype:'fp32'`) or WASM
// (`device:'wasm'`, `dtype:'q8'`), selected via the constructor. Synthesis is delegated to `TtsWorker` and the
// streamed PCM chunks are re-exposed as the uniform `AudioChunk` async-iterable. The worker is injected via a factory
// so unit tests can substitute a fake and never spawn a real Worker / load weights.

import type { AudioChunk, TtsProvider, TtsProviderId, VoiceSynthesisOptions } from "../TtsProvider";
import type { VoiceModelDtype } from "../VoiceManifest";
import type { MainToWorkerMessage, WorkerDevice, WorkerToMainMessage } from "../TtsWorkerProtocol";

/** Minimal worker surface the provider depends on; the real `Worker` and test fakes both satisfy it. */
export interface TtsWorkerLike {
	postMessage(message: MainToWorkerMessage): void;
	addEventListener(type: "message", listener: (event: MessageEvent<WorkerToMainMessage>) => void): void;
	addEventListener(type: "error", listener: (event: { readonly message?: string }) => void): void;
	removeEventListener(type: "message", listener: (event: MessageEvent<WorkerToMainMessage>) => void): void;
	removeEventListener(type: "error", listener: (event: { readonly message?: string }) => void): void;
	terminate(): void;
}

export type TtsWorkerFactory = () => TtsWorkerLike;

const defaultWorkerFactory: TtsWorkerFactory = () =>
	// Vite bundles the worker from this URL; the cast bridges the real `Worker` to the narrow surface used here.
	new Worker(new URL("../TtsWorker.ts", import.meta.url), { type: "module" }) as unknown as TtsWorkerLike;

export interface KokoroProviderOptions {
	readonly device: WorkerDevice;
	readonly modelId: string;
	readonly dtype: VoiceModelDtype;
	readonly workerFactory?: TtsWorkerFactory;
}

const providerIdByDevice: Record<WorkerDevice, TtsProviderId> = {
	webgpu: "kokoro-webgpu",
	wasm: "kokoro-wasm",
};

export class KokoroProvider implements TtsProvider {
	readonly id: TtsProviderId;
	readonly producesPcm = true;

	private readonly options: KokoroProviderOptions;
	private readonly workerFactory: TtsWorkerFactory;
	private worker: TtsWorkerLike | undefined;
	private nextRequestId = 1;

	constructor(options: KokoroProviderOptions) {
		this.options = options;
		this.id = providerIdByDevice[options.device];
		this.workerFactory = options.workerFactory ?? defaultWorkerFactory;
	}

	/** Spawns the worker and loads the model; rejects if the worker reports an init error so the runtime can fall back. */
	async init(): Promise<void> {
		const worker = this.workerFactory();
		this.worker = worker;

		await new Promise<void>((resolve, reject) => {
			const onMessage = (event: MessageEvent<WorkerToMainMessage>): void => {
				const message = event.data;
				if (message.type === "ready") {
					cleanup();
					resolve();
				} else if (message.type === "error") {
					cleanup();
					reject(new Error(message.message));
				}
			};

			const onError = (event: { readonly message?: string }): void => {
				cleanup();
				reject(new Error(event.message ?? "TTS worker crashed during init"));
			};

			const cleanup = (): void => {
				worker.removeEventListener("message", onMessage);
				worker.removeEventListener("error", onError);
			};

			worker.addEventListener("message", onMessage);
			worker.addEventListener("error", onError);
			worker.postMessage({ type: "init", modelId: this.options.modelId, device: this.options.device, dtype: this.options.dtype });
		});
	}

	/** Streams synthesized PCM chunks for `text`. Throws if the worker reports an error mid-stream (→ runtime fallback). */
	async *synthesize(text: string, options?: VoiceSynthesisOptions): AsyncIterable<AudioChunk> {
		const worker = this.worker;
		if (!worker) {
			throw new Error("KokoroProvider.synthesize called before init()");
		}

		const requestId = this.nextRequestId++;
		const queue: AudioChunk[] = [];
		let finished = false;
		let failure: Error | undefined;
		let notify: (() => void) | undefined;

		const wake = (): void => {
			notify?.();
			notify = undefined;
		};

		const onMessage = (event: MessageEvent<WorkerToMainMessage>): void => {
			const message = event.data;
			if ("requestId" in message && message.requestId !== undefined && message.requestId !== requestId) {
				return;
			}

			if (message.type === "chunk") {
				queue.push({ pcm: message.pcm, sampleRate: message.sampleRate });
			} else if (message.type === "done") {
				finished = true;
			} else if (message.type === "error") {
				failure = new Error(message.message);
			}

			wake();
		};

		const onError = (event: { readonly message?: string }): void => {
			failure = new Error(event.message ?? "TTS worker crashed during synthesis");
			wake();
		};

		worker.addEventListener("message", onMessage);
		worker.addEventListener("error", onError);

		try {
			worker.postMessage({ type: "synthesize", requestId, text, voiceId: options?.voiceId, rate: options?.rate });

			for (;;) {
				const next = queue.shift();
				if (next !== undefined) {
					yield next;
					continue;
				}

				if (failure) {
					throw failure;
				}

				if (finished) {
					return;
				}

				// biome-ignore lint/performance/noAwaitInLoops: pull-queue bridge — wait for the next worker chunk before yielding again.
				await new Promise<void>((resolve) => {
					notify = resolve;
				});
			}
		} finally {
			worker.removeEventListener("message", onMessage);
			worker.removeEventListener("error", onError);
		}
	}

	/** Signals the worker to cancel the current request (main-thread playback stop drives the actual barge-in). */
	stop(): void {
		this.worker?.postMessage({ type: "cancel", requestId: this.nextRequestId });
	}

	/** Terminates the worker and releases the model session. */
	dispose(): void {
		this.worker?.terminate();
		this.worker = undefined;
	}
}
