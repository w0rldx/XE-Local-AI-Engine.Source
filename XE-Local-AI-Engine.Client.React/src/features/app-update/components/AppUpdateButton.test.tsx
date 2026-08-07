// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/features/app-update/queries/useAppUpdate", () => ({
	noBodyOptions: {},
	useAppUpdateStatus: vi.fn(),
	useApplyAppUpdate: vi.fn(),
	useProbeAppUpdateStatus: vi.fn(),
}));

import {
	useApplyAppUpdate,
	useAppUpdateStatus,
	useProbeAppUpdateStatus,
} from "@/features/app-update/queries/useAppUpdate";
import { AppUpdateButton } from "./AppUpdateButton";

describe("AppUpdateButton", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addListener: vi.fn(),
				removeListener: vi.fn(),
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		vi.mocked(useAppUpdateStatus).mockReturnValue({
			data: {
				isDesktop: true,
				isConfigured: true,
				updateAvailable: true,
				currentVersion: "0.1.0",
				availableVersion: "0.1.1",
			},
		} as never);
		vi.mocked(useProbeAppUpdateStatus).mockReturnValue({ mutateAsync: vi.fn() } as never);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("does not poll for a restart when the live apply reports no update", async () => {
		const mutateAsync = vi.fn().mockResolvedValue({ applying: false });
		vi.mocked(useApplyAppUpdate).mockReturnValue({ mutateAsync } as never);
		const fetchSpy = vi.spyOn(globalThis, "fetch");
		render(<MantineProvider><AppUpdateButton /></MantineProvider>);

		fireEvent.click(screen.getByRole("button", { name: /update now/i }));

		await waitFor(() => expect(mutateAsync).toHaveBeenCalledOnce());
		expect(screen.queryByText(/restarting/i)).toBeNull();
		expect(fetchSpy).not.toHaveBeenCalled();
	});

	it("renders the up-to-date state while idle without removing the restart-state owner", () => {
		vi.mocked(useAppUpdateStatus).mockReturnValue({
			data: {
				isDesktop: true,
				isConfigured: true,
				updateAvailable: false,
				currentVersion: "0.1.1",
				availableVersion: null,
			},
		} as never);
		vi.mocked(useApplyAppUpdate).mockReturnValue({ mutateAsync: vi.fn() } as never);

		render(<MantineProvider><AppUpdateButton /></MantineProvider>);

		expect(screen.getByText(/up to date/i)).toBeTruthy();
		expect(screen.queryByRole("button", { name: /update now/i })).toBeNull();
	});

	it("starts health polling only after the backend confirms the update was scheduled", async () => {
		vi.useFakeTimers();
		try {
			const mutateAsync = vi.fn().mockResolvedValue({ applying: true });
			vi.mocked(useApplyAppUpdate).mockReturnValue({ mutateAsync } as never);
			const fetchSpy = vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("host restarting"));
			render(<MantineProvider><AppUpdateButton /></MantineProvider>);

			await act(async () => {
				fireEvent.click(screen.getByRole("button", { name: /update now/i }));
				await Promise.resolve();
			});

			expect(screen.getByText(/restarting/i)).toBeTruthy();
			expect(fetchSpy).not.toHaveBeenCalled();
			await act(async () => {
				await vi.advanceTimersByTimeAsync(2000);
			});
			expect(fetchSpy).toHaveBeenCalledWith("/health/live", { cache: "no-store" });
		} finally {
			vi.useRealTimers();
		}
	});

	it("waits for the expected version when the old host remains healthy during shutdown", async () => {
		vi.useFakeTimers();
		try {
			vi.mocked(useApplyAppUpdate).mockReturnValue({
				mutateAsync: vi.fn().mockResolvedValue({ applying: true }),
			} as never);
			const refreshStatus = vi
				.fn()
				.mockResolvedValueOnce({ currentVersion: "0.1.0" })
				.mockResolvedValueOnce({ currentVersion: "0.1.1" });
			vi.mocked(useProbeAppUpdateStatus).mockReturnValue({ mutateAsync: refreshStatus } as never);
			const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue({ ok: true } as Response);
			render(<MantineProvider><AppUpdateButton /></MantineProvider>);

			await act(async () => {
				fireEvent.click(screen.getByRole("button", { name: /update now/i }));
				await Promise.resolve();
				await vi.advanceTimersByTimeAsync(2000);
			});

			expect(fetchSpy).toHaveBeenCalledTimes(1);
			expect(refreshStatus).toHaveBeenCalledTimes(1);
			expect(screen.getByText(/restarting/i)).toBeTruthy();

			await act(async () => {
				await vi.advanceTimersByTimeAsync(2000);
			});

			expect(fetchSpy).toHaveBeenCalledTimes(2);
			expect(refreshStatus).toHaveBeenCalledTimes(2);
			expect(screen.queryByText(/restarting/i)).toBeNull();
		} finally {
			vi.useRealTimers();
		}
	});
});
