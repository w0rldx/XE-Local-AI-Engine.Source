import { describe, expect, it } from "vitest";

import { downsampleToInt16 } from "@/features/transcription/capture/Pcm16DownsamplerWorklet.js";

/**
 * This imports the worklet FILE, not a copy of its arithmetic, so it exercises the bytes that ship. The import is
 * harmless under vitest only because the worklet guards BOTH the processor class and its registration behind
 * `typeof AudioWorkletProcessor !== "undefined"` — guarding the registration alone still evaluates
 * `extends AudioWorkletProcessor` and throws a ReferenceError at import time.
 *
 * It is also the worklet's only incoming edge that a human reads: `PcmCapture.ts` imports it for its URL alone.
 */
describe("downsampleToInt16", () => {
	// Plan §4.3: Downsample_AtRatioOne_IsAPassthrough
	it("is a passthrough at ratio 1, which is what a honoured 16 kHz request gives", () => {
		const input = Float32Array.from([0, 0.5, -0.5, 1]);
		const out = new Int16Array(4);

		const advanced = downsampleToInt16(input, 1, 0, out, 0);

		expect([...out]).toEqual([0, Math.round(0.5 * 32767), Math.round(-0.5 * 32767), 32767]);
		expect(advanced.outIndex).toBe(4);
		expect(advanced.cursor).toBe(4);
	});

	// Plan §4.3: Downsample_At48kTo16k_KeepsEveryThirdSample
	it("keeps every third sample at 48 kHz to 16 kHz", () => {
		const input = Float32Array.from([0.1, 0.9, -0.9, 0.2, 0.8, -0.8, 0.3, 0.7, -0.7]);
		const out = new Int16Array(3);

		const advanced = downsampleToInt16(input, 48_000 / 16_000, 0, out, 0);

		expect([...out]).toEqual([
			Math.round(Math.fround(0.1) * 32767),
			Math.round(Math.fround(0.2) * 32767),
			Math.round(Math.fround(0.3) * 32767),
		]);
		expect(advanced.outIndex).toBe(3);
	});

	// Plan §4.3: Downsample_ClampsBeyondFullScale
	it("clamps beyond full scale rather than wrapping around", () => {
		const input = Float32Array.from([2, -2]);
		const out = new Int16Array(2);

		downsampleToInt16(input, 1, 0, out, 0);

		expect(out[0]).toBe(32767);
		expect(out[1]).toBe(-32768);
	});

	it("stops when the frame is full and reports where to resume", () => {
		const input = Float32Array.from([0.1, 0.2, 0.3, 0.4]);
		const out = new Int16Array(2);

		const advanced = downsampleToInt16(input, 1, 0, out, 0);

		expect(advanced.outIndex).toBe(2);
		expect(advanced.cursor).toBe(2);
		expect(out[1]).toBe(Math.round(Math.fround(0.2) * 32767));
	});

	// The fractional cursor is what stops a non-integer ratio from drifting across render quanta: a 44.1 kHz
	// context consumes 2.75625 input samples per output sample, so whole-sample bookkeeping would lose a sample
	// every few quanta and slowly shift the transcript's own clock.
	it("carries a fractional cursor across a call so a non-integer ratio does not drift", () => {
		const input = Float32Array.from([0, 1, 0, -1, 0, 1]);
		const out = new Int16Array(3);

		const advanced = downsampleToInt16(input, 2.5, 0, out, 0);

		expect(advanced.cursor).toBe(7.5);
		expect(advanced.outIndex).toBe(3);
		// The third output reads at 5.0, the last real sample; the second interpolates halfway between -1 and 0.
		expect(out[0]).toBe(0);
		expect(out[1]).toBe(Math.round(-0.5 * 32767));
		expect(out[2]).toBe(32767);
	});

	it("holds the last sample flat rather than reading past the end of a quantum", () => {
		const input = Float32Array.from([0, 0.5]);
		const out = new Int16Array(2);

		downsampleToInt16(input, 1.5, 0, out, 0);

		// The second output reads at 1.5, half a sample past the end; the lower neighbour is held.
		expect(out[1]).toBe(Math.round(Math.fround(0.5) * 32767));
	});
});
