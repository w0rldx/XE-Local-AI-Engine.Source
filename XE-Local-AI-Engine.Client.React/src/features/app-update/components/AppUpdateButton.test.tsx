// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/features/app-update/queries/useAppUpdate", () => ({
	useAppUpdateStatus: vi.fn(),
	useApplyAppUpdate: vi.fn(),
	useProbeAppUpdateStatus: vi.fn(),
}));

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { useApplyAppUpdate, useAppUpdateStatus, useProbeAppUpdateStatus } from "@/features/app-update/queries/useAppUpdate";
import { AppUpdateButton } from "./AppUpdateButton";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

describe("AppUpdateButton", () => {
	beforeEach(() => {
		// The busy confirm dialog is a Mantine modal, whose scroll area needs ResizeObserver.
		installJsdomEnvironmentMocks();
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
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<AppUpdateButton />
			</MantineProvider>,
		);

		fireEvent.click(screen.getByRole("button", { name: /update now/i }));

		await waitFor(() => expect(mutateAsync).toHaveBeenCalledOnce());
		expect(screen.queryByText(/restarting/i)).toBeNull();
		expect(fetchSpy).not.toHaveBeenCalled();
	});

	it("lists the running work a refused apply reports and re-sends with force only after the operator confirms", async () => {
		const busyBody = {
			busyItems: [
				{ kind: "trainingRun", displayName: null },
				{ kind: "modelDownload", displayName: "qwen3-8b.gguf" },
			],
			message: "Updating restarts XE and stops the work that is running now.",
		} as unknown as ProblemDetails;
		const mutateAsync = vi.fn().mockRejectedValueOnce(new ApiError(409, busyBody)).mockResolvedValueOnce({ applying: false });
		vi.mocked(useApplyAppUpdate).mockReturnValue({ mutateAsync } as never);
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<AppUpdateButton />
			</MantineProvider>,
		);

		fireEvent.click(screen.getByRole("button", { name: /update now/i }));

		await waitFor(() => expect(screen.getByText("Model download: qwen3-8b.gguf")).toBeTruthy());
		expect(screen.getByText("Training run")).toBeTruthy();
		expect(mutateAsync).toHaveBeenCalledOnce();
		expect(mutateAsync).toHaveBeenLastCalledWith({ body: { force: false } });

		fireEvent.click(screen.getByTestId("app-update-busy-confirm"));

		await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(2));
		expect(mutateAsync).toHaveBeenLastCalledWith({ body: { force: true } });
		await waitFor(() => expect(screen.queryByText("Model download: qwen3-8b.gguf")).toBeNull());
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

		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<AppUpdateButton />
			</MantineProvider>,
		);

		expect(screen.getByText(/up to date/i)).toBeTruthy();
		expect(screen.queryByRole("button", { name: /update now/i })).toBeNull();
	});

	it("starts health polling only after the backend confirms the update was scheduled", async () => {
		vi.useFakeTimers();
		try {
			const mutateAsync = vi.fn().mockResolvedValue({ applying: true });
			vi.mocked(useApplyAppUpdate).mockReturnValue({ mutateAsync } as never);
			const fetchSpy = vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("host restarting"));
			render(
				<MantineProvider env="test" theme={testMantineTheme}>
					<AppUpdateButton />
				</MantineProvider>,
			);

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
			render(
				<MantineProvider env="test" theme={testMantineTheme}>
					<AppUpdateButton />
				</MantineProvider>,
			);

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
