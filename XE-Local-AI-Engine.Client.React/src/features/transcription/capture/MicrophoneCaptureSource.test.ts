import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CaptureError, type CaptureFrame } from "@/features/transcription/capture/CaptureSource";
import { MicrophoneCaptureSource } from "@/features/transcription/capture/MicrophoneCaptureSource";

/**
 * jsdom has neither `navigator.mediaDevices` nor `AudioContext` (`installJsdomEnvironmentMocks` stubs neither), so
 * both are stubbed here and restored afterwards. The graph stub is what makes the happy path testable at all: it
 * records the connections `PcmCapture` makes, which is the only place the zero-gain-to-destination rule can be
 * checked outside a browser.
 */

interface StubTrack {
	stop: () => void;
	stopped: boolean;
}

function createTrack(): StubTrack {
	const track: StubTrack = {
		stopped: false,
		stop: () => {
			track.stopped = true;
		},
	};
	return track;
}

function createStream(tracks: StubTrack[]): MediaStream {
	return { getTracks: () => tracks, getAudioTracks: () => tracks, getVideoTracks: () => [] } as unknown as MediaStream;
}

class StubAudioNode {
	readonly connections: StubAudioNode[] = [];
	disconnectCalls = 0;

	connect<T extends StubAudioNode>(target: T): T {
		this.connections.push(target);
		return target;
	}

	disconnect(): void {
		this.disconnectCalls += 1;
	}
}

class StubGainNode extends StubAudioNode {
	readonly gain = { value: 1 };
}

class StubWorkletNode extends StubAudioNode {
	readonly port: { onmessage: ((event: MessageEvent<ArrayBuffer>) => void) | null } = { onmessage: null };
	readonly processorName: string;
	readonly processorOptions: Record<string, unknown>;

	constructor(_context: unknown, processorName: string, options: { processorOptions?: Record<string, unknown> }) {
		super();
		this.processorName = processorName;
		this.processorOptions = options.processorOptions ?? {};
	}
}

let requestedSampleRate: number | undefined;
let addedModules: string[] = [];
let closeCalls = 0;
let lastSource: StubAudioNode | undefined;
let lastGain: StubGainNode | undefined;
let lastWorkletNode: StubWorkletNode | undefined;

class StubAudioContext {
	readonly destination = new StubAudioNode();
	readonly audioWorklet = {
		addModule: (url: string): Promise<void> => {
			addedModules.push(url);
			return Promise.resolve();
		},
	};

	constructor(options?: { sampleRate?: number }) {
		requestedSampleRate = options?.sampleRate;
	}

	createMediaStreamSource(): StubAudioNode {
		lastSource = new StubAudioNode();
		return lastSource;
	}

	createGain(): StubGainNode {
		lastGain = new StubGainNode();
		return lastGain;
	}

	close(): Promise<void> {
		closeCalls += 1;
		return Promise.resolve();
	}
}

/**
 * A context that starts suspended — what the autoplay policy gives a graph built before the page's first user
 * gesture — and refuses to resume. `state` is absent from the stub above, so the resume path is only walked here.
 */
class SuspendedAudioContext extends StubAudioContext {
	readonly state = "suspended";

	resume(): Promise<void> {
		return Promise.reject(new Error("resume refused by the stub"));
	}
}

function installAudioGraph(): void {
	vi.stubGlobal("AudioContext", StubAudioContext);
	vi.stubGlobal(
		"AudioWorkletNode",
		class extends StubWorkletNode {
			constructor(context: unknown, processorName: string, options: { processorOptions?: Record<string, unknown> }) {
				super(context, processorName, options);
				lastWorkletNode = this;
			}
		},
	);
}

function installMediaDevices(mediaDevices: unknown): void {
	Object.defineProperty(navigator, "mediaDevices", { value: mediaDevices, configurable: true, writable: true });
}

function failWith(name: string): Error {
	const error = new Error(`${name} raised by the stub`);
	error.name = name;
	return error;
}

beforeEach(() => {
	requestedSampleRate = undefined;
	addedModules = [];
	closeCalls = 0;
	lastSource = undefined;
	lastGain = undefined;
	lastWorkletNode = undefined;
});

afterEach(() => {
	Reflect.deleteProperty(navigator, "mediaDevices");
});

describe("MicrophoneCaptureSource", () => {
	it("reports 'unsupported' in an insecure context, where mediaDevices is absent", async () => {
		const source = new MicrophoneCaptureSource("mono");

		await expect(source.start(() => undefined)).rejects.toMatchObject({ code: "unsupported" });
	});

	it("maps a refused permission to 'permission-denied'", async () => {
		installMediaDevices({ getUserMedia: () => Promise.reject(failWith("NotAllowedError")) });
		const source = new MicrophoneCaptureSource("mono");

		const rejection = await source.start(() => undefined).catch((error: unknown) => error);

		expect(rejection).toBeInstanceOf(CaptureError);
		expect(rejection).toMatchObject({ code: "permission-denied" });
	});

	it("maps a blocked secure-context call to 'permission-denied'", async () => {
		installMediaDevices({ getUserMedia: () => Promise.reject(failWith("SecurityError")) });

		await expect(new MicrophoneCaptureSource("you").start(() => undefined)).rejects.toMatchObject({
			code: "permission-denied",
		});
	});

	it("maps a missing or over-constrained device to 'no-device'", async () => {
		installMediaDevices({ getUserMedia: () => Promise.reject(failWith("NotFoundError")) });
		await expect(new MicrophoneCaptureSource("mono").start(() => undefined)).rejects.toMatchObject({
			code: "no-device",
		});

		installMediaDevices({ getUserMedia: () => Promise.reject(failWith("OverconstrainedError")) });
		await expect(new MicrophoneCaptureSource("mono").start(() => undefined)).rejects.toMatchObject({
			code: "no-device",
		});
	});

	// The browser's own echoCancellation / noiseSuppression defaults are what a meeting participant expects, and
	// whisper is not fed through WebRTC processing — so the constraints carry the device and nothing else.
	it("asks for the chosen device exactly and overrides no audio processing", async () => {
		const getUserMedia = vi.fn(() => Promise.resolve(createStream([createTrack()])));
		installMediaDevices({ getUserMedia });
		installAudioGraph();

		await new MicrophoneCaptureSource("you", "device-7").start(() => undefined);

		expect(getUserMedia).toHaveBeenCalledWith({ audio: { deviceId: { exact: "device-7" } } });
	});

	it("asks for any microphone when no device was chosen", async () => {
		const getUserMedia = vi.fn(() => Promise.resolve(createStream([createTrack()])));
		installMediaDevices({ getUserMedia });
		installAudioGraph();

		await new MicrophoneCaptureSource("mono").start(() => undefined);

		expect(getUserMedia).toHaveBeenCalledWith({ audio: true });
	});

	it("routes the worklet through a zero-gain node to the destination and asks for 16 kHz", async () => {
		installMediaDevices({ getUserMedia: () => Promise.resolve(createStream([createTrack()])) });
		installAudioGraph();

		await new MicrophoneCaptureSource("mono").start(() => undefined);

		expect(requestedSampleRate).toBe(16_000);
		expect(addedModules).toHaveLength(1);
		expect(lastWorkletNode?.processorName).toBe("xe-pcm16-downsampler");
		expect(lastWorkletNode?.processorOptions).toEqual({ targetSampleRate: 16_000, frameMs: 250 });
		// source -> worklet -> zero gain -> destination. Without the path to the destination the node is never
		// pulled; without the zero gain the microphone is played back through the speakers.
		expect(lastSource?.connections[0]).toBe(lastWorkletNode);
		expect(lastWorkletNode?.connections[0]).toBe(lastGain);
		expect(lastGain?.gain.value).toBe(0);
		expect(lastGain?.connections).toHaveLength(1);
	});

	it("hands each posted buffer to the callback as an Int16Array on its own channel", async () => {
		installMediaDevices({ getUserMedia: () => Promise.resolve(createStream([createTrack()])) });
		installAudioGraph();
		const frames: CaptureFrame[] = [];

		await new MicrophoneCaptureSource("you").start((frame) => frames.push(frame));
		const pcm = Int16Array.from([1, -1, 32_767]);
		lastWorkletNode?.port.onmessage?.({ data: pcm.buffer } as unknown as MessageEvent<ArrayBuffer>);

		expect(frames).toHaveLength(1);
		expect(frames[0]?.channel).toBe("you");
		expect([...(frames[0]?.pcm ?? [])]).toEqual([1, -1, 32_767]);
	});

	it("stops the microphone when the worklet module fails, and reports 'worklet-failed'", async () => {
		const track = createTrack();
		installMediaDevices({ getUserMedia: () => Promise.resolve(createStream([track])) });
		installAudioGraph();
		vi.stubGlobal(
			"AudioContext",
			class extends StubAudioContext {
				override readonly audioWorklet = {
					addModule: (): Promise<void> => Promise.reject(failWith("AbortError")),
				};
			},
		);

		await expect(new MicrophoneCaptureSource("mono").start(() => undefined)).rejects.toMatchObject({
			code: "worklet-failed",
		});
		expect(track.stopped).toBe(true);
		expect(closeCalls).toBe(1);
	});

	// The resume sits after the graph is wired, so a rejection there used to escape with the context still open —
	// one leaked AudioContext per failed attempt, with nothing left holding a reference to close it.
	it("closes the audio context when the suspended graph refuses to resume", async () => {
		const track = createTrack();
		installMediaDevices({ getUserMedia: () => Promise.resolve(createStream([track])) });
		installAudioGraph();
		vi.stubGlobal("AudioContext", SuspendedAudioContext);

		await expect(new MicrophoneCaptureSource("mono").start(() => undefined)).rejects.toMatchObject({
			code: "worklet-failed",
		});
		expect(closeCalls).toBe(1);
		expect(track.stopped).toBe(true);
	});

	it("stops every track and closes the context on stop, and a second stop does nothing more", async () => {
		const track = createTrack();
		installMediaDevices({ getUserMedia: () => Promise.resolve(createStream([track])) });
		installAudioGraph();
		const source = new MicrophoneCaptureSource("mono");

		await source.start(() => undefined);
		await source.stop();
		await source.stop();

		expect(track.stopped).toBe(true);
		expect(closeCalls).toBe(1);
		expect(lastWorkletNode?.disconnectCalls).toBe(1);
		expect(lastWorkletNode?.port.onmessage).toBeNull();
	});

	it("tolerates a stop before a start", async () => {
		const source = new MicrophoneCaptureSource("mono");

		await expect(source.stop()).resolves.toBeUndefined();
	});
});
