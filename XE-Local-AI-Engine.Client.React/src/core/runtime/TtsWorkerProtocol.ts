// Message contract between the main thread (`KokoroProvider`) and the dedicated TTS worker (`TtsWorker`).
// Kept in its own module so both sides share one source of truth and tests can build typed fake messages.

import type { VoiceModelDtype } from "./VoiceManifest";

/** WebGPU or WASM execution path for Kokoro/onnxruntime inside the worker. */
export type WorkerDevice = "webgpu" | "wasm";

export interface InitWorkerMessage {
	readonly type: "init";
	readonly modelId: string;
	readonly device: WorkerDevice;
	readonly dtype: VoiceModelDtype;
}

export interface SynthesizeWorkerMessage {
	readonly type: "synthesize";
	readonly requestId: number;
	readonly text: string;
	readonly voiceId?: string;
	readonly rate?: number;
}

export interface CancelWorkerMessage {
	readonly type: "cancel";
	readonly requestId: number;
}

export type MainToWorkerMessage = InitWorkerMessage | SynthesizeWorkerMessage | CancelWorkerMessage;

export interface ReadyWorkerMessage {
	readonly type: "ready";
}

export interface ChunkWorkerMessage {
	readonly type: "chunk";
	readonly requestId: number;
	readonly pcm: Float32Array;
	readonly sampleRate: number;
}

export interface DoneWorkerMessage {
	readonly type: "done";
	readonly requestId: number;
}

export interface ErrorWorkerMessage {
	readonly type: "error";
	readonly requestId?: number;
	readonly message: string;
}

export type WorkerToMainMessage = ReadyWorkerMessage | ChunkWorkerMessage | DoneWorkerMessage | ErrorWorkerMessage;
