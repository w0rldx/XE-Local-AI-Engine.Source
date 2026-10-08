// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { installPreloadErrorReload } from "@/core/integrations/vite/PreloadErrorReload";

function dispatchPreloadError(): Event {
	const event = new Event("vite:preloadError", { cancelable: true });
	window.dispatchEvent(event);
	return event;
}

// The listener is installed once per page, as Main.tsx does; a second install would double every assertion below.
installPreloadErrorReload();

describe("installPreloadErrorReload", () => {
	const reload = vi.fn();

	beforeEach(() => {
		sessionStorage.clear();
		reload.mockClear();
		vi.stubGlobal("location", { ...window.location, reload });
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
	});

	it("reloads once on a stale chunk and lets a repeat inside the window reach the error boundary", () => {
		const first = dispatchPreloadError();
		const second = dispatchPreloadError();

		expect(reload).toHaveBeenCalledTimes(1);
		expect(first.defaultPrevented).toBe(true);
		expect(second.defaultPrevented).toBe(false);
	});

	it("does not reload when storage is blocked, since nothing could stop a loop", () => {
		vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
			throw new DOMException("blocked", "SecurityError");
		});

		const event = dispatchPreloadError();

		expect(reload).not.toHaveBeenCalled();
		expect(event.defaultPrevented).toBe(false);
	});
});
