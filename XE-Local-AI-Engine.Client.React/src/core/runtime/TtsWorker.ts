// Dedicated ES-module Web Worker running Kokoro TTS off the main thread (plan §3.4, §7.2).
//
// A dedicated worker is mandatory: the onnxruntime-web WebGPU execution provider forbids ORT's `wasm.proxy`, so the
// only way to keep synthesis off the UI thread is to own this worker. Audio is streamed back chunk-by-chunk as
// transferable PCM. Any failure posts an `{type:"error"}` message so `VoiceRuntime` can fall back down the ladder
// instead of hanging. This file is the worker entry — it is bundled by Vite via `new Worker(new URL(...))`.

import { env, KokoroTTS, TextSplitterStream } from "kokoro-js";

import { installOrtWarningFilter } from "./OrtLogFilter";
import type { MainToWorkerMessage, WorkerToMainMessage } from "./TtsWorkerProtocol";

// Silence onnxruntime-web's benign Warning-level session-assignment noise in this worker's console. Installed for the
// worker's lifetime (no restore); ORT errors and all other output still pass through. See OrtLogFilter for why the
// proper logSeverityLevel route is unreachable through kokoro-js.
installOrtWarningFilter(globalThis.console);

// The standard DOM lib types `self` as a Window; in a worker it is a DedicatedWorkerGlobalScope. Rather than pull the
// WebWorker lib (which conflicts with DOM in this shared tsconfig), model only the two members the worker uses.
const workerScope = globalThis as unknown as {
	postMessage(message: WorkerToMainMessage, transfer?: Transferable[]): void;
	addEventListener(type: "message", listener: (event: MessageEvent<MainToWorkerMessage>) => void): void;
};

// Point onnxruntime-web at the self-hosted `/ort` directory (copied by vite-plugin-static-copy) so it never reaches
// out to a CDN for its WASM binaries. kokoro-js re-exports a THIN `env` whose only member is a `wasmPaths` accessor
// that proxies onnxruntime-web's `backends.onnx.wasm.wasmPaths` — this wrapper has no `backends` property, so reaching
// through `env.backends.onnx…` throws "Cannot read properties of undefined (reading 'onnx')". Set the accessor directly.
(env as unknown as { wasmPaths: string }).wasmPaths = "/ort/";

type StreamOptions = Parameters<KokoroTTS["stream"]>[1];

let model: KokoroTTS | undefined;

function post(message: WorkerToMainMessage, transfer?: Transferable[]): void {
	workerScope.postMessage(message, transfer);
}

function describeError(error: unknown): string {
	return error instanceof Error ? error.message : String(error);
}

async function handleInit(message: Extract<MainToWorkerMessage, { type: "init" }>): Promise<void> {
	try {
		model = await KokoroTTS.from_pretrained(message.modelId, { dtype: message.dtype, device: message.device });
		post({ type: "ready" });
	} catch (error) {
		post({ type: "error", message: describeError(error) });
	}
}

async function handleSynthesize(message: Extract<MainToWorkerMessage, { type: "synthesize" }>): Promise<void> {
	const activeModel = model;
	if (!activeModel) {
		post({ type: "error", requestId: message.requestId, message: "TTS model not initialized" });
		return;
	}

	try {
		const splitter = new TextSplitterStream();
		const streamOptions = { voice: message.voiceId, speed: message.rate } as unknown as StreamOptions;
		const stream = activeModel.stream(splitter, streamOptions);
		splitter.push(message.text);
		splitter.close();

		for await (const { audio } of stream) {
			// `audio` is a Transformers.js RawAudio — `.audio` is the Float32Array PCM, `.sampling_rate` the rate.
			// Copy into a fresh array so the transferred buffer is exactly the chunk (the source may be a sub-view).
			const pcm = new Float32Array(audio.audio);
			post({ type: "chunk", requestId: message.requestId, pcm, sampleRate: audio.sampling_rate }, [pcm.buffer]);
		}

		post({ type: "done", requestId: message.requestId });
	} catch (error) {
		post({ type: "error", requestId: message.requestId, message: describeError(error) });
	}
}

workerScope.addEventListener("message", (event) => {
	const message = event.data;
	if (message.type === "init") {
		// `handle*` never reject (they post `error` internally); the `.catch` is a defensive last resort.
		handleInit(message).catch((error: unknown) => post({ type: "error", message: describeError(error) }));
		return;
	}

	if (message.type === "synthesize") {
		handleSynthesize(message).catch((error: unknown) =>
			post({ type: "error", requestId: message.requestId, message: describeError(error) }),
		);
	}

	// "cancel" is accepted for forward-compatibility; M1 barge-in is handled by stopping playback on the main thread.
});
