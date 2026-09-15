// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { CaptureControls } from "@/features/transcription/components/CaptureControls";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderControls(overrides: Partial<Parameters<typeof CaptureControls>[0]> = {}) {
	const onStart = vi.fn();
	const onStop = vi.fn();
	renderWithProviders(
		<CaptureControls
			sourceKind="Microphone"
			state="idle"
			error={null}
			replayStalled={false}
			connected={true}
			subscribeFailed={null}
			elapsedMs={0}
			onStart={onStart}
			onStop={onStop}
			{...overrides}
		/>,
	);
	return { onStart, onStop };
}

describe("CaptureControls", () => {
	afterEach(() => {
		cleanup();
	});

	it("starts capture from the click, with nothing between the gesture and the handler", () => {
		const { onStart } = renderControls();

		fireEvent.click(screen.getByTestId("transcription-capture-start"));

		expect(onStart).toHaveBeenCalledTimes(1);
		expect(screen.queryByTestId("transcription-capture-stop")).toBeNull();
	});

	it("offers stop while capturing", () => {
		const { onStop } = renderControls({ state: "capturing" });

		fireEvent.click(screen.getByTestId("transcription-capture-stop"));

		expect(onStop).toHaveBeenCalledTimes(1);
		expect(screen.queryByTestId("transcription-capture-start")).toBeNull();
	});

	// Offering stop while the sources are still being acquired would race the acquisition it is meant to undo, so the
	// start control stays in place and busy instead.
	it("keeps the start control busy rather than offering stop while starting", () => {
		renderControls({ state: "starting" });

		expect(screen.getByTestId("transcription-capture-start")).toHaveProperty("disabled", true);
		expect(screen.queryByTestId("transcription-capture-stop")).toBeNull();
	});

	// Wall-clock time and audio time diverge; the transcript's own clock is the one an operator can check against the
	// timestamps beside it.
	it("counts the captured audio, not the wall clock", () => {
		renderControls({ state: "capturing", elapsedMs: 93_400 });

		expect(screen.getByTestId("transcription-capture-elapsed").textContent).toBe("01:33");
	});

	it("names both lanes on a microphone-plus-system session", () => {
		renderControls({ sourceKind: "MicrophoneAndSystem" });

		expect(screen.getByText("You")).toBeDefined();
		expect(screen.getByText("Others")).toBeDefined();
	});

	it("names the single source it is capturing", () => {
		renderControls({ sourceKind: "SystemAudio" });

		expect(screen.getByText("System audio")).toBeDefined();
	});

	// R19: a shared surface with no audio is the failure an operator can actually act on, so the alert says what to do
	// rather than naming the browser's own untranslated error.
	it("explains a shared surface that came back without audio", () => {
		renderControls({ error: "no-audio-track" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain("The shared screen came back without audio.");
	});

	it("explains an overloaded node without claiming the transcript was lost", () => {
		renderControls({ error: "overloaded" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain("Everything committed so far is kept.");
	});

	// A node refusal is neither a browser capability nor a device problem; reporting it as "unsupported" would send the
	// operator looking for a browser bug.
	it("distinguishes a refused session from an unsupported browser", () => {
		renderControls({ error: "start-failed" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain(
			"The node refused to open this live session.",
		);
	});

	it("warns that the transcript may be incomplete when the replay drain stalled", () => {
		renderControls({ state: "capturing", replayStalled: true });

		expect(screen.getByTestId("transcription-replay-stalled").textContent).toContain("may be incomplete");
	});

	// M1/m5: the hub subscription is what makes a capture possible. Offering Start before it is up acquires the
	// devices and then fails on the first frame a quarter of a second later, reported as a send failure.
	it("offers no start until the hub subscription is up, and says why", () => {
		renderControls({ connected: false });

		expect(screen.getByTestId("transcription-capture-start")).toHaveProperty("disabled", true);
		expect(screen.getByTestId("transcription-capture-not-ready").textContent).toContain(
			"start becomes available once the live connection is ready",
		);
	});

	it("enables start once the hub subscription is up", () => {
		renderControls({ connected: true });

		expect(screen.getByTestId("transcription-capture-start")).toHaveProperty("disabled", false);
		expect(screen.queryByTestId("transcription-capture-not-ready")).toBeNull();
	});

	// A node with transcription switched off refuses the subscription and nothing retries it, so this alert is the
	// only thing that ever tells the operator why the live transcript stayed empty.
	it("names a refused subscription the node explained", () => {
		renderControls({ connected: false, subscribeFailed: { code: "transcription-disabled" } });

		expect(screen.getByTestId("transcription-capture-subscribe-error").textContent).toContain(
			"Transcription is switched off on this node.",
		);
	});

	it("falls back to one sentence for a refusal the node did not name", () => {
		renderControls({ connected: false, subscribeFailed: { code: "transcription-subscribe-failed" } });

		expect(screen.getByTestId("transcription-capture-subscribe-error").textContent).toContain(
			"did not open the live transcript for this session",
		);
	});

	// M2: a dead transport and a node that cannot keep up both stop capture, but only one of them is a performance
	// problem — and the string is the whole of what the operator acts on.
	it("distinguishes a lost connection from a node that could not keep up", () => {
		renderControls({ error: "disconnected" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain("The live connection to this node was lost");
	});

	it("shows no error banner while nothing has failed", () => {
		renderControls({ state: "capturing" });

		expect(screen.queryByTestId("transcription-capture-error")).toBeNull();
		expect(screen.queryByTestId("transcription-replay-stalled")).toBeNull();
	});
});
