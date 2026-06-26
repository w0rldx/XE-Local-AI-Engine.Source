// @vitest-environment jsdom

import { afterEach, describe, expect, it } from "vitest";

import { detectWasm, detectWebGpu } from "./CapabilityDetector";

interface FakeDevice {
	readonly lost: Promise<unknown>;
}

interface FakeAdapter {
	requestDevice(): Promise<FakeDevice>;
}

function stubNavigatorGpu(gpu: unknown): void {
	Object.defineProperty(navigator, "gpu", { value: gpu, configurable: true });
}

function clearNavigatorGpu(): void {
	if ("gpu" in navigator) {
		Reflect.deleteProperty(navigator as object, "gpu");
	}
}

describe("CapabilityDetector.detectWebGpu", () => {
	afterEach(() => {
		clearNavigatorGpu();
	});

	it("returns false and falls back when navigator.gpu is absent", async () => {
		clearNavigatorGpu();

		await expect(detectWebGpu()).resolves.toBe(false);
	});

	it("does not throw and returns false when requestAdapter resolves null", async () => {
		stubNavigatorGpu({ requestAdapter: () => Promise.resolve(null) });

		await expect(detectWebGpu()).resolves.toBe(false);
	});

	it("returns true when an adapter and device are obtained", async () => {
		const adapter: FakeAdapter = { requestDevice: () => Promise.resolve({ lost: Promise.resolve() }) };
		stubNavigatorGpu({ requestAdapter: () => Promise.resolve(adapter) });

		await expect(detectWebGpu()).resolves.toBe(true);
	});

	it("returns false when requestDevice rejects", async () => {
		const adapter: FakeAdapter = { requestDevice: () => Promise.reject(new Error("no device")) };
		stubNavigatorGpu({ requestAdapter: () => Promise.resolve(adapter) });

		await expect(detectWebGpu()).resolves.toBe(false);
	});
});

describe("CapabilityDetector.detectWasm", () => {
	it("detects the WebAssembly API in a standard environment", () => {
		expect(detectWasm()).toBe(true);
	});
});
