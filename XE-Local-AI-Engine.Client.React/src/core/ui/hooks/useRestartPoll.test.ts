// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useRestartPoll } from "@/core/ui/hooks/useRestartPoll";

const ok = { ok: true } as Response;

describe("useRestartPoll", () => {
	let reload: ReturnType<typeof vi.fn<() => void>>;

	beforeEach(() => {
		vi.useFakeTimers();
		reload = vi.fn<() => void>();
		vi.spyOn(window, "location", "get").mockReturnValue({ ...window.location, reload });
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.restoreAllMocks();
	});

	// One probe every 2 s; the async advance lets each probe's await settle before the next timer fires.
	async function poll(times: number): Promise<void> {
		await act(async () => {
			await vi.advanceTimersByTimeAsync(2000 * times);
		});
	}

	it("reloads on the first healthy answer the caller confirms, by default on /health/live", async () => {
		const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(ok);
		const { result } = renderHook(() => useRestartPoll());

		act(() => result.current.start(async () => true));
		await poll(1);

		expect(fetchSpy).toHaveBeenCalledWith("/health/live", { cache: "no-store" });
		expect(reload).toHaveBeenCalledOnce();
		expect(result.current.status).toBe("ready");
	});

	it("probes the given path and keeps polling while the answering host is not the new one", async () => {
		const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(ok);
		const isBack = vi.fn<() => Promise<boolean>>().mockResolvedValueOnce(false).mockResolvedValue(true);
		const { result } = renderHook(() => useRestartPoll());

		act(() => result.current.start(isBack, { probePath: "/probe" }));
		await poll(1);

		expect(fetchSpy).toHaveBeenCalledWith("/probe", { cache: "no-store" });
		expect(reload).not.toHaveBeenCalled();
		expect(result.current.status).toBe("restarting");

		await poll(1);

		expect(reload).toHaveBeenCalledOnce();
	});
});
