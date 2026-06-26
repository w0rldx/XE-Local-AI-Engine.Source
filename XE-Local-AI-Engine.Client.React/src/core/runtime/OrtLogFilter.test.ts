import { describe, expect, it, vi } from "vitest";

import { installOrtWarningFilter, isBenignOrtWarning } from "@/core/runtime/OrtLogFilter";

describe("isBenignOrtWarning", () => {
	it("flags ORT warning-level lines", () => {
		expect(
			isBenignOrtWarning([
				"2026-06-26 22:03:23 [W:onnxruntime:, session_state.cc:1280 VerifyEachNodeIsAssignedToAnEp] Some nodes…",
			]),
		).toBe(true);
	});

	it("does not flag ORT error-level lines", () => {
		expect(isBenignOrtWarning(["[E:onnxruntime:, something bad happened]"])).toBe(false);
	});

	it("does not flag unrelated or non-string output", () => {
		expect(isBenignOrtWarning(["a normal log line"])).toBe(false);
		expect(isBenignOrtWarning([{ not: "a string" }])).toBe(false);
		expect(isBenignOrtWarning([])).toBe(false);
	});
});

describe("installOrtWarningFilter", () => {
	function makeConsole() {
		return { log: vi.fn(), warn: vi.fn(), error: vi.fn() };
	}

	it("drops benign ORT warnings across log/warn/error", () => {
		const target = makeConsole();
		// Capture the underlying spies before install replaces target.* with the filtering wrappers.
		const spies = { log: target.log, warn: target.warn, error: target.error };
		installOrtWarningFilter(target);

		target.warn("[W:onnxruntime:, VerifyEachNodeIsAssignedToAnEp] noise");
		target.error("[W:onnxruntime:, Rerunning with verbose output] noise");
		target.log("[W:onnxruntime:, more] noise");

		expect(spies.warn).not.toHaveBeenCalled();
		expect(spies.error).not.toHaveBeenCalled();
		expect(spies.log).not.toHaveBeenCalled();
	});

	it("passes through ORT errors and normal output", () => {
		const target = makeConsole();
		const spies = { log: target.log, warn: target.warn, error: target.error };
		installOrtWarningFilter(target);

		target.error("[E:onnxruntime:, real failure]");
		target.log("hello");

		expect(spies.error).toHaveBeenCalledWith("[E:onnxruntime:, real failure]");
		expect(spies.log).toHaveBeenCalledWith("hello");
	});

	it("restores the original methods when the returned disposer runs", () => {
		const target = makeConsole();
		const original = target.warn;
		const restore = installOrtWarningFilter(target);
		expect(target.warn).not.toBe(original);

		restore();
		expect(target.warn).toBe(original);
	});
});
