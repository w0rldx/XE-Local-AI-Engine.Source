// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/features/app-update/queries/useAppUpdate", () => ({
	useAppUpdateStatus: vi.fn(),
	useRefreshAppUpdateStatus: vi.fn(),
}));

vi.mock("@/features/app-update/components/AppUpdateButton", () => ({
	AppUpdateButton: () => <div data-testid="app-update-button" />,
}));

import {
	useAppUpdateStatus,
	useRefreshAppUpdateStatus,
} from "@/features/app-update/queries/useAppUpdate";
import { AppUpdateSection } from "./AppUpdateSection";

function setup(overrides: Record<string, unknown> = {}) {
	vi.mocked(useAppUpdateStatus).mockReturnValue({
		data: {
			currentVersion: "0.1.0-rc.2",
			availableVersion: null,
			updateAvailable: false,
			isConfigured: true,
			isDesktop: true,
			checkStatus: "ready",
			lastCheckedUtc: 1_700_000_000_000,
			...overrides,
		},
	} as never);
	vi.mocked(useRefreshAppUpdateStatus).mockReturnValue({ mutate: vi.fn(), isPending: false } as never);
}

describe("AppUpdateSection", () => {
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
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class { observe = vi.fn(); unobserve = vi.fn(); disconnect = vi.fn(); },
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("offers anonymous update checks without any GitHub sign-in UI", () => {
		setup();
		render(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.getByRole("button", { name: /check for updates/i })).toBeTruthy();
		expect(screen.queryByText(/sign in with github/i)).toBeNull();
	});

	it("shows the update button when the public feed has an update", () => {
		setup({ updateAvailable: true, availableVersion: "0.1.0-rc.3" });
		render(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.getByTestId("app-update-button")).toBeTruthy();
	});

	it("withholds controls when the artifact has no public source", () => {
		setup({ isConfigured: false });
		render(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.queryByRole("button", { name: /check for updates/i })).toBeNull();
		expect(screen.getByText(/automatic updates aren't available in this build/i)).toBeTruthy();
	});

	it("distinguishes an offline feed from a failed feed", () => {
		setup({ checkStatus: "offline" });
		const { rerender } = render(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.getByText(/couldn't reach the update service/i)).toBeTruthy();

		setup({ checkStatus: "failed" });
		rerender(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.getByText(/couldn't process the update information/i)).toBeTruthy();
		expect(screen.queryByText(/couldn't reach the update service/i)).toBeNull();
	});

	it("renders nothing outside desktop mode", () => {
		setup({ isDesktop: false });
		render(<MantineProvider><AppUpdateSection /></MantineProvider>);

		expect(screen.queryByText(/^Updates$/)).toBeNull();
	});
});
