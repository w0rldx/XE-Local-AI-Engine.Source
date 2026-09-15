/**
 * The Web Audio graph both browser capture sources share (S4 plan §2.3): one `AudioContext` per stream, the
 * `Pcm16DownsamplerWorklet` on it, and a disposer that takes the whole thing down.
 */

import { CaptureError } from "@/features/transcription/capture/CaptureSource";

import workletUrl from "@/features/transcription/capture/Pcm16DownsamplerWorklet.js?url";

const WORKLET_NAME = "xe-pcm16-downsampler";
const TARGET_SAMPLE_RATE = 16_000;
/** 4 000 samples = 8 000 bytes, well under the hub's 32 KB frame cap. */
const FRAME_MS = 250;

/**
 * Starts feeding 16 kHz mono int16 frames from `stream` to `onFrame`.
 *
 * @returns a disposer that stops every track, tears the graph down and closes the context. Idempotent.
 * @throws {CaptureError} `worklet-failed` when the context or the worklet module cannot be created.
 */
export async function startPcmCapture(stream: MediaStream, onFrame: (pcm: Int16Array) => void): Promise<() => Promise<void>> {
	const context = createAudioContext();
	// Every throw path from here on is inside the guard: once the context exists, the only way it gets closed on a
	// failure is `closeQuietly`, and a start that leaks one leaks it per attempt with nothing left holding it.
	try {
		await context.audioWorklet.addModule(workletUrl);
		const node = new AudioWorkletNode(context, WORKLET_NAME, {
			numberOfInputs: 1,
			numberOfOutputs: 1,
			// `outputChannelCount` governs only the output. Under the default "max" mode a stereo source keeps both
			// input channels and the processor, which reads `inputs[0][0]`, would drop the right one — including any
			// speech only present there. Explicit mono makes the graph downmix (L+R)/2 before the worklet sees it.
			channelCount: 1,
			channelCountMode: "explicit",
			channelInterpretation: "speakers",
			outputChannelCount: [1],
			processorOptions: { targetSampleRate: TARGET_SAMPLE_RATE, frameMs: FRAME_MS },
		});

		// The worklet transfers the buffer, so nothing is copied across the thread boundary.
		node.port.onmessage = (event: MessageEvent<ArrayBuffer>) => onFrame(new Int16Array(event.data));

		const source = context.createMediaStreamSource(stream);
		const silentGain = context.createGain();
		// Load-bearing twice over: a Web Audio node is only pulled when it reaches the destination, and a gain of 0
		// is what stops the microphone from being played back through the speakers. `numberOfOutputs: 0` is the
		// tempting shorter version and is not reliably scheduled.
		silentGain.gain.value = 0;
		source.connect(node);
		node.connect(silentGain).connect(context.destination);
		// A context created before the page's first user gesture starts suspended under the autoplay policy, and a
		// suspended graph pulls nothing: no frames, no partials, no error. Resuming is a no-op on a running context.
		if (context.state === "suspended") {
			await context.resume();
		}

		let disposed = false;
		return async () => {
			if (disposed) {
				return;
			}
			disposed = true;
			for (const track of stream.getTracks()) {
				track.stop();
			}
			node.port.onmessage = null;
			source.disconnect();
			node.disconnect();
			silentGain.disconnect();
			await closeQuietly(context);
		};
	} catch (error) {
		await closeQuietly(context);
		throw new CaptureError("worklet-failed", `The PCM downsampler worklet did not start: ${describe(error)}`);
	}
}

/**
 * The requested sample rate is a request, not a guarantee: MDN documents a `NotSupportedError` for a rate the
 * hardware cannot serve. The worklet resamples from the context's real rate regardless, so the fallback costs
 * nothing but a ratio that is no longer 1.
 */
function createAudioContext(): AudioContext {
	try {
		return new AudioContext({ sampleRate: TARGET_SAMPLE_RATE });
	} catch (error) {
		if (error instanceof Error && error.name === "NotSupportedError") {
			return new AudioContext();
		}
		throw new CaptureError("worklet-failed", `The audio context could not be created: ${describe(error)}`);
	}
}

/** A context that refuses to close must not mask the failure that is already being reported. */
async function closeQuietly(context: AudioContext): Promise<void> {
	try {
		await context.close();
	} catch {
		// Nothing left to release, and the caller is already unwinding.
	}
}

function describe(error: unknown): string {
	return error instanceof Error ? `${error.name}: ${error.message}` : String(error);
}
