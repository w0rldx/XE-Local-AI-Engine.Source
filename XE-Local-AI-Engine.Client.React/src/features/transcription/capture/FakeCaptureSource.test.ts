import { describe, expect, it } from "vitest";

import { CaptureError, type CaptureFrame } from "@/features/transcription/capture/CaptureSource";
import { FakeCaptureSource } from "@/features/transcription/capture/FakeCaptureSource";

/**
 * The fake is a test seam, but the orchestration tests that consume it are only as trustworthy as it is: a fake
 * whose `emit` silently dropped frames, or whose `failWith` resolved anyway, would turn those tests green over a
 * broken capture path. So the seam itself is checked here.
 */
describe("FakeCaptureSource", () => {
	it("records the start and emits frames on its own channel", async () => {
		const frames: CaptureFrame[] = [];
		const source = new FakeCaptureSource("others");

		await source.start((frame) => frames.push(frame));
		source.emit(Int16Array.from([7, -7]));

		expect(source.started).toBe(true);
		expect(source.startCalls).toBe(1);
		expect(frames).toHaveLength(1);
		expect(frames[0]?.channel).toBe("others");
		expect([...(frames[0]?.pcm ?? [])]).toEqual([7, -7]);
	});

	it("emits nothing before a start or after a stop", async () => {
		const frames: CaptureFrame[] = [];
		const source = new FakeCaptureSource("you");

		source.emit(Int16Array.from([1]));
		await source.start((frame) => frames.push(frame));
		await source.stop();
		source.emit(Int16Array.from([2]));

		expect(frames).toHaveLength(0);
		expect(source.stopped).toBe(true);
	});

	it("rejects with the configured capture error and never reports started", async () => {
		const failure = new CaptureError("no-audio-track", "the stub refused");
		const source = new FakeCaptureSource("others", { failWith: failure });

		await expect(source.start(() => undefined)).rejects.toBe(failure);
		expect(source.started).toBe(false);
		expect(source.startCalls).toBe(1);
	});
});
