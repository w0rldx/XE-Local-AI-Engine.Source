/**
 * The AudioWorklet that turns whatever rate the browser gave us into the 16 kHz mono int16 frames the
 * transcription hub accepts (S4 plan §2.3).
 *
 * ONE self-contained file with no module dependencies of any kind, and it must stay that way. `PcmCapture.ts`
 * pulls it in for its URL alone, through Vite's `?url` suffix: the asset plugin emits this file's raw bytes and
 * does not traverse its module graph, so a sibling module would ship as an unresolved specifier,
 * `registerProcessor` would never run, and the failure would surface only in a browser as a `worklet-failed`
 * capture error with every build gate still green. The slice gate greps the emitted asset for the module-loading
 * keyword and requires zero hits, so keep that keyword out of this file's prose too — a comment that spells it out
 * turns the one mechanical proof there is into noise.
 *
 * Both the class and its registration sit inside the worklet-global guard: `extends AudioWorkletProcessor` is
 * evaluated when the class declaration executes, so a class at module scope throws
 * `ReferenceError: AudioWorkletProcessor is not defined` under vitest before any registration guard could run.
 * With the guard, loading this file from a test defines only `downsampleToInt16`.
 *
 * ponytail: linear interpolation, not a polyphase resampler — whisper's own front end is a 16 kHz mel filterbank
 * and the aliasing at 48k→16k is far below its noise floor; swap in a windowed-sinc kernel only if a live round
 * shows accuracy loss versus the same clip uploaded as a file.
 */

/**
 * Resamples `input` into `out` until either the input is exhausted or the frame is full, writing clamped int16.
 *
 * @param {Float32Array} input one render quantum of mono float samples, in [-1, 1].
 * @param {number} ratio input samples consumed per output sample (`sampleRate / targetSampleRate`; 1 is a copy).
 * @param {number} cursor fractional read position into `input`; carries the remainder across render quanta.
 * @param {Int16Array} out the frame being filled.
 * @param {number} outIndex the next slot to write in `out`.
 * @returns {{ cursor: number, outIndex: number }} the advanced read cursor and write index.
 */
export function downsampleToInt16(input, ratio, cursor, out, outIndex) {
	let readCursor = cursor;
	let writeIndex = outIndex;
	while (readCursor < input.length && writeIndex < out.length) {
		const left = Math.floor(readCursor);
		const weight = readCursor - left;
		const lower = input[left] ?? 0;
		// The last sample of a quantum has no right-hand neighbour yet; holding it flat is a sub-sample error at a
		// boundary, which the mel filterbank cannot see.
		const upper = left + 1 < input.length ? (input[left + 1] ?? 0) : lower;
		const sample = lower + (upper - lower) * weight;
		out[writeIndex] = Math.max(-32768, Math.min(32767, Math.round(sample * 32767)));
		writeIndex += 1;
		readCursor += ratio;
	}
	return { cursor: readCursor, outIndex: writeIndex };
}

if (typeof AudioWorkletProcessor !== "undefined" && typeof registerProcessor === "function") {
	class Pcm16Downsampler extends AudioWorkletProcessor {
		constructor(options) {
			super();
			const processorOptions = options?.processorOptions ?? {};
			const targetSampleRate = processorOptions.targetSampleRate ?? 16000;
			const frameMs = processorOptions.frameMs ?? 250;
			// `sampleRate` is an AudioWorkletGlobalScope global carrying the context's REAL rate, whether or not the
			// 16 kHz request was honoured. A honoured request makes this 1 and the resample a copy.
			this.ratio = sampleRate / targetSampleRate;
			this.frameSamples = Math.round((targetSampleRate * frameMs) / 1000);
			this.frame = new Int16Array(this.frameSamples);
			this.frameIndex = 0;
			this.cursor = 0;
		}

		process(inputs) {
			const input = inputs[0]?.[0];
			// A muted or ended track hands us nothing; killing the processor would end the session silently.
			if (!input) {
				return true;
			}
			// `input.length` is read rather than assumed to be 128: the render-quantum size is spec'd today but
			// documented as subject to change.
			let readCursor = this.cursor;
			while (readCursor < input.length) {
				const advanced = downsampleToInt16(input, this.ratio, readCursor, this.frame, this.frameIndex);
				readCursor = advanced.cursor;
				this.frameIndex = advanced.outIndex;
				if (this.frameIndex === this.frame.length) {
					const full = this.frame.buffer;
					// Transferred, not copied: the frame crosses the thread boundary without an allocation on the
					// audio thread's critical path.
					this.port.postMessage(full, [full]);
					this.frame = new Int16Array(this.frameSamples);
					this.frameIndex = 0;
				}
			}
			this.cursor = readCursor - input.length;
			return true;
		}
	}

	registerProcessor("xe-pcm16-downsampler", Pcm16Downsampler);
}
