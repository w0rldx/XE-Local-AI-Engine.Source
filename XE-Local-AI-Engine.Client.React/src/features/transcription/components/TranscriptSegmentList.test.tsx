// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { TranscriptSegmentList } from "@/features/transcription/components/TranscriptSegmentList";
import type { TranscriptSegmentView } from "@/features/transcription/models/TranscriptionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function segment(overrides: Partial<TranscriptSegmentView> = {}): TranscriptSegmentView {
	return {
		id: "segment-1",
		seq: 1,
		startMs: 0,
		endMs: 1_000,
		text: "the first line",
		channel: "Mono",
		confidence: null,
		...overrides,
	};
}

describe("TranscriptSegmentList", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders one row per segment in the order it is given", () => {
		renderWithProviders(
			<TranscriptSegmentList
				segments={[
					segment({ id: "a", seq: 1, text: "first" }),
					segment({ id: "b", seq: 2, text: "second" }),
					segment({ id: "c", seq: 3, text: "third" }),
				]}
			/>,
		);

		const rows = screen.getAllByTestId(/^transcript-segment-\d+$/);
		expect(rows.map((row) => row.textContent)).toEqual([
			expect.stringContaining("first"),
			expect.stringContaining("second"),
			expect.stringContaining("third"),
		]);
	});

	// mm:ss.S, and the tenth is the point: the reader scrubs to the offset, so 61.25 s must read 01:01.2, not "1 min".
	it("formats each boundary as a mm:ss.S clip offset", () => {
		renderWithProviders(<TranscriptSegmentList segments={[segment({ startMs: 61_250, endMs: 605_900 })]} />);

		expect(screen.getByTestId("transcript-segment-1").textContent).toContain("01:01.2 – 10:05.9");
	});

	it("shows a channel badge for You and Others but never for Mono", () => {
		renderWithProviders(
			<TranscriptSegmentList
				segments={[
					segment({ id: "a", seq: 1, channel: "Mono" }),
					segment({ id: "b", seq: 2, channel: "You" }),
					segment({ id: "c", seq: 3, channel: "Others" }),
				]}
			/>,
		);

		expect(screen.queryByTestId("transcript-segment-channel-1")).toBeNull();
		expect(screen.getByTestId("transcript-segment-channel-2").textContent).toBe("You");
		expect(screen.getByTestId("transcript-segment-channel-3").textContent).toBe("Others");
	});
});
