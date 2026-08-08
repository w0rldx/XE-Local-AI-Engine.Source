import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import {
	getRrwebSegment,
	packRrwebEvent,
	startRrwebRecording,
	stopRrwebRecording,
	unpackRrwebEvent,
} from "@/core/diagnostics/RrwebRecorder";

// Capture the options rrweb's `record` is called with, without loading the real library (hermetic).
const { recordMock } = vi.hoisted(() => ({ recordMock: vi.fn() }));
vi.mock("rrweb", () => ({
	record: (options: unknown) => {
		recordMock(options);
		return () => undefined;
	},
}));

interface CapturedOptions {
	emit: (event: unknown, isCheckout?: boolean) => void;
	packFn: (event: { type: number; data: unknown; timestamp: number }) => string;
	checkoutEveryNms: number;
	maskAllInputs: boolean;
	maskTextSelector: string;
	maskTextFn: (text: string) => string;
	blockClass: string;
}

function lastOptions(): CapturedOptions {
	const call = recordMock.mock.calls.at(-1);
	if (!call) {
		throw new Error("record was not called");
	}
	return call[0] as CapturedOptions;
}

beforeEach(() => {
	recordMock.mockClear();
	stopRrwebRecording();
	useDeveloperModeStore.setState({ developerMode: false });
});

afterEach(() => {
	stopRrwebRecording();
});

describe("packRrwebEvent / unpackRrwebEvent", () => {
	it("round-trips an event through the @rrweb/packer v1 format", () => {
		const event = { type: 3, data: { source: 2, text: "hello" }, timestamp: 1_700_000_000 };

		const packed = packRrwebEvent(event);

		expect(typeof packed).toBe("string");
		expect(unpackRrwebEvent(packed)).toEqual(event);
	});
});

describe("rrweb_onlyWhenDevMode", () => {
	it("records nothing and yields an empty segment when Developer Mode is off", async () => {
		useDeveloperModeStore.setState({ developerMode: false });

		await startRrwebRecording();

		expect(recordMock).not.toHaveBeenCalled();
		expect(getRrwebSegment()).toEqual([]);
	});

	it("starts recording when Developer Mode is on", async () => {
		useDeveloperModeStore.setState({ developerMode: true });

		await startRrwebRecording();

		expect(recordMock).toHaveBeenCalledTimes(1);
	});
});

describe("RrwebRecorder.masksRenderedText", () => {
	it("pins the privacy-masking record config", async () => {
		useDeveloperModeStore.setState({ developerMode: true });

		await startRrwebRecording();
		const options = lastOptions();

		expect(options.maskTextSelector).toBe("*");
		expect(options.maskAllInputs).toBe(true);
		expect(options.blockClass).toBe("rr-block");
		expect(options.checkoutEveryNms).toBe(30_000);
		expect(typeof options.maskTextFn).toBe("function");
	});

	it("masks rendered text so a message body never reaches the packed segment", async () => {
		const messageBody = "TOP-SECRET conversation contents 42";
		useDeveloperModeStore.setState({ developerMode: true });

		await startRrwebRecording();
		const options = lastOptions();

		// Sanity: the raw body WOULD appear in a serialized event if it were not masked.
		expect(JSON.stringify({ text: messageBody })).toContain(messageBody);

		// The configured mask path replaces rendered text with bullets — body gone, length preserved.
		const masked = options.maskTextFn(messageBody);
		expect(masked).not.toContain(messageBody);
		expect(masked).toBe("•".repeat(messageBody.length));

		// rrweb masks before emit; simulate an emitted (masked) full-snapshot event through the
		// configured packFn + emit, then assert the body survives nowhere in the captured segment.
		const maskedEvent = { type: 2, data: { node: { textContent: masked } }, timestamp: Date.now() };
		options.emit(options.packFn(maskedEvent), true);

		const segment = getRrwebSegment();
		expect(segment.length).toBeGreaterThan(0);
		for (const packed of segment) {
			expect(packed).not.toContain(messageBody);
			expect(JSON.stringify(unpackRrwebEvent(packed))).not.toContain(messageBody);
		}
	});
});

describe("getRrwebSegment ring buffer", () => {
	it("retains only the most recent checkout segments", async () => {
		useDeveloperModeStore.setState({ developerMode: true });

		await startRrwebRecording();
		const { emit, packFn } = lastOptions();

		// Three checkout-delimited segments; only the most recent two are retained (MAX_SEGMENTS = 2).
		emit(packFn({ type: 2, data: { seg: "a" }, timestamp: 1 }), true);
		emit(packFn({ type: 3, data: { seg: "a-evt" }, timestamp: 2 }), false);
		emit(packFn({ type: 2, data: { seg: "b" }, timestamp: 3 }), true);
		emit(packFn({ type: 2, data: { seg: "c" }, timestamp: 4 }), true);

		const decoded = getRrwebSegment().map((packed) => JSON.stringify(unpackRrwebEvent(packed)));
		expect(decoded.some((event) => event.includes('"seg":"a"'))).toBe(false);
		expect(decoded.some((event) => event.includes('"seg":"b"'))).toBe(true);
		expect(decoded.some((event) => event.includes('"seg":"c"'))).toBe(true);
	});

	it("clears the segment on stop", async () => {
		useDeveloperModeStore.setState({ developerMode: true });

		await startRrwebRecording();
		lastOptions().emit(packRrwebEvent({ type: 2, data: {}, timestamp: 1 }), true);
		expect(getRrwebSegment().length).toBeGreaterThan(0);

		stopRrwebRecording();

		expect(getRrwebSegment()).toEqual([]);
	});
});
