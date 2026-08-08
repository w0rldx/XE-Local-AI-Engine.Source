// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const baseStatus = {
	currentVersion: "0.1.0",
	availableVersion: "0.1.1",
	updateAvailable: true,
	isConfigured: true,
	isDesktop: true,
	checkStatus: "ready",
	lastCheckedUtc: 1_700_000_000_000,
};
const statusQuery = vi.hoisted(() => vi.fn());

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
	getAppUpdateStatusQueryKey: vi.fn((options) => [{ _id: "getAppUpdateStatus", query: options?.query }]),
	getAppUpdateStatusOptions: vi.fn((options) => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getAppUpdateStatus", query: options?.query }],
		queryFn: statusQuery,
	})),
	applyAppUpdateMutation: vi.fn(() => ({ mutationFn: async () => ({ applying: true }) })),
}));

vi.mock("@/core/api/generated/sdk.gen", () => ({ getAppUpdateStatus: vi.fn() }));
vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (options: unknown) => options,
	callWithResponseValidation: <T,>(call: Promise<T>) => call,
}));

import { getAppUpdateStatus } from "@/core/api/generated/sdk.gen";
import { AppUpdateSection } from "./AppUpdateSection";

describe("AppUpdateSection restart polling", () => {
	beforeEach(() => {
		statusQuery.mockReset().mockResolvedValue(baseStatus);
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
		vi.spyOn(globalThis, "fetch").mockResolvedValue({ ok: true } as Response);
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
		vi.restoreAllMocks();
	});

	it("keeps polling when the old healthy host reports a cleared update, then reloads for the target version", async () => {
		vi.mocked(getAppUpdateStatus)
			.mockResolvedValueOnce({ data: { ...baseStatus, availableVersion: null, updateAvailable: false } } as never)
			.mockResolvedValueOnce({
				data: { ...baseStatus, currentVersion: "0.1.1", availableVersion: null, updateAvailable: false },
			} as never);
		const queryClient = new QueryClient({
			defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
		});
		render(
			<QueryClientProvider client={queryClient}>
				<MantineProvider><AppUpdateSection /></MantineProvider>
			</QueryClientProvider>,
		);
		await waitFor(() => expect(screen.getByRole("button", { name: /update now/i })).toBeTruthy());

		vi.useFakeTimers();
		await act(async () => {
			fireEvent.click(screen.getByRole("button", { name: /update now/i }));
			await Promise.resolve();
			await vi.advanceTimersByTimeAsync(2000);
		});

		expect(screen.getByText(/restarting/i)).toBeTruthy();
		const statusKey = [{
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			_id: "getAppUpdateStatus",
			query: { refresh: null },
		}];
		expect(queryClient.getQueryData(statusKey)).toMatchObject({
			availableVersion: "0.1.1",
			updateAvailable: true,
		});
		statusQuery.mockResolvedValue({ ...baseStatus, availableVersion: null, updateAvailable: false });
		await act(async () => {
			await queryClient.refetchQueries({ queryKey: statusKey });
		});
		expect(screen.getByText(/restarting/i)).toBeTruthy();

		await act(async () => {
			await vi.advanceTimersByTimeAsync(2000);
		});

		expect(getAppUpdateStatus).toHaveBeenCalledTimes(2);
	});
});
