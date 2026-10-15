/**
 * Types for the hand-written `Pcm16DownsamplerWorklet.js`. The worklet is plain `.js` because Vite's `?url` import
 * hands the file over as an asset without a TypeScript transform; this declaration is what lets its one exported
 * pure function be called from a typed test.
 */

/**
 * Resamples `input` into `out` until either the input is exhausted or the frame is full, writing clamped int16.
 *
 * @param input one render quantum of mono float samples, in [-1, 1].
 * @param ratio input samples consumed per output sample (`sampleRate / targetSampleRate`; 1 is a copy).
 * @param cursor fractional read position into `input`; carries the remainder across render quanta.
 * @param out the frame being filled.
 * @param outIndex the next slot to write in `out`.
 * @returns the advanced read cursor and write index.
 */
export declare function downsampleToInt16(
	input: Float32Array,
	ratio: number,
	cursor: number,
	out: Int16Array,
	outIndex: number,
): { readonly cursor: number; readonly outIndex: number };
