// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { CaptureControls } from "@/features/transcription/components/CaptureControls";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderControls(overrides: Partial<Parameters<typeof CaptureControls>[0]> = {}) {
	const onStart = vi.fn();
	const onStop = vi.fn();
	const onCancel = vi.fn();
	renderWithProviders(
		<CaptureControls
			sourceKind="Microphone"
			state="idle"
			error={null}
			replayStalled={false}
			connected={true}
			subscribeFailed={null}
			elapsedMs={0}
			bufferedMs={null}
			runtimeState={null}
			onStart={onStart}
			onStop={onStop}
			onCancel={onCancel}
			{...overrides}
		/>,
	);
	return { onStart, onStop, onCancel };
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

	it("names finalization and offers cancellation while graceful stop is pending", () => {
		const { onCancel } = renderControls({ state: "stopping" });

		expect(screen.getByTestId("transcription-capture-finalizing").textContent).toBe("Finalizing transcript…");
		fireEvent.click(screen.getByTestId("transcription-capture-cancel"));
		expect(onCancel).toHaveBeenCalledTimes(1);
		expect(screen.queryByTestId("transcription-capture-stop")).toBeNull();
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

	// B2: a node that falls behind buffers and catches up; the operator sees by how much instead of a stopped session.
	it("says how far behind the transcription is while capturing", () => {
		renderControls({ state: "capturing", bufferedMs: 4200 });

		expect(screen.getByTestId("transcription-capture-behind").textContent).toBe("Transcribing… 5 s behind");
		expect(screen.queryByTestId("transcription-capture-error")).toBeNull();
	});

	it("shows no behind counter once the node has caught up", () => {
		renderControls({ state: "capturing", bufferedMs: 0 });

		expect(screen.queryByTestId("transcription-capture-behind")).toBeNull();
	});

	// B2: Stop drains the whole backlog. The finalizing control counts it down; Cancel stays as the escape hatch.
	it("counts down the backlog a stop is draining", () => {
		const { onCancel } = renderControls({ state: "stopping", bufferedMs: 12_001 });

		expect(screen.getByTestId("transcription-capture-finalizing").textContent).toBe("Finishing the last 13 s…");
		fireEvent.click(screen.getByTestId("transcription-capture-cancel"));
		expect(onCancel).toHaveBeenCalledTimes(1);
	});

	// A3: the first live start can spend a minute on the runtime download and model load. The line says which.
	it.each([
		{ runtimeState: "stopped" as const, expected: "Preparing the transcription runtime… this can take a minute on first use" },
		{ runtimeState: "starting" as const, expected: "Loading the model…" },
		{ runtimeState: "ready" as const, expected: "Starting…" },
		{ runtimeState: null, expected: "Starting…" },
	])("names what a pending start waits on when the runtime is $runtimeState", ({ runtimeState, expected }) => {
		renderControls({ state: "starting", runtimeState });

		expect(screen.getByTestId("transcription-capture-start-status").textContent).toBe(expected);
	});

	it("shows no start status once capture is running", () => {
		renderControls({ state: "capturing", runtimeState: "stopped" });

		expect(screen.queryByTestId("transcription-capture-start-status")).toBeNull();
	});

	// A node refusal is neither a browser capability nor a device problem; reporting it as "unsupported" would send the
	// operator looking for a browser bug.
	it("distinguishes a refused session from an unsupported browser", () => {
		renderControls({ error: "start-failed" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain(
			"The node refused to open this live session.",
		);
	});

	// The node's three typed capture refusals each need a different thing from the operator, so each renders its own
	// sentence from the shipped bundle rather than the generic "the node refused" one.
	it.each([
		{ error: "capture-not-supported" as const, expected: "Windows 10 build 20348 or later" },
		{ error: "session-not-live" as const, expected: "was not open on the node" },
		{ error: "capture-already-running" as const, expected: "already capturing an application" },
	])("names what to do about a $error refusal", ({ error, expected }) => {
		renderControls({ error });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain(expected);
	});

	// A stop the node never confirmed is not a finished session. The sentence has to say the retry is coming and that
	// leaving the page would abandon it, which "the node refused to open this live session" does not.
	it("tells the operator a stop the node never confirmed will be retried", () => {
		renderControls({ error: "stop-failed" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain("never confirmed it");
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

	// M2: a dead transport is the one thing that stops capture on its own, and the string says so.
	it("names a lost connection as the reason capture stopped", () => {
		renderControls({ error: "disconnected" });

		expect(screen.getByTestId("transcription-capture-error").textContent).toContain("The live connection to this node was lost");
	});

	it("shows no error banner while nothing has failed", () => {
		renderControls({ state: "capturing" });

		expect(screen.queryByTestId("transcription-capture-error")).toBeNull();
		expect(screen.queryByTestId("transcription-replay-stalled")).toBeNull();
	});
});
