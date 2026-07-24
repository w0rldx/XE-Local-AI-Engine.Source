// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Isolate the section from the network and from its children's own hooks.
vi.mock("@/features/app-update/queries/useAppUpdate", () => ({
	noBodyOptions: {},
	useAppUpdateStatus: vi.fn(),
	useRefreshAppUpdateStatus: vi.fn(),
	useSignOutGitHubAuth: vi.fn(),
}));

vi.mock("@/features/app-update/components/GitHubSignInCard", () => ({
	GitHubSignInCard: () => <div data-testid="github-sign-in-card" />,
}));

vi.mock("@/features/app-update/components/AppUpdateButton", () => ({
	AppUpdateButton: () => <div data-testid="app-update-button" />,
}));

import {
	useAppUpdateStatus,
	useRefreshAppUpdateStatus,
	useSignOutGitHubAuth,
} from "@/features/app-update/queries/useAppUpdate";
import { AppUpdateSection } from "./AppUpdateSection";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return (
			<QueryClientProvider client={queryClient}>
				<MantineProvider>{children}</MantineProvider>
			</QueryClientProvider>
		);
	}
	return render(ui, { wrapper: Wrapper });
}

// A status payload shaped like AppUpdateStatusResponse, with the auth state under test.
function statusOf(authState: string, overrides: Record<string, unknown> = {}) {
	return {
		currentVersion: "0.1.0-rc.2",
		availableVersion: null,
		updateAvailable: false,
		authState,
		login: null,
		isDesktop: true,
		isOffline: false,
		lastCheckedUtc: 1_700_000_000_000,
		...overrides,
	};
}

function setupMocks(authState: string, overrides: Record<string, unknown> = {}) {
	vi.mocked(useAppUpdateStatus).mockReturnValue({
		data: statusOf(authState, overrides),
	} as never);
	vi.mocked(useRefreshAppUpdateStatus).mockReturnValue({
		mutate: vi.fn(),
		mutateAsync: vi.fn(),
		isPending: false,
	} as never);
	vi.mocked(useSignOutGitHubAuth).mockReturnValue({
		mutate: vi.fn(),
		mutateAsync: vi.fn(),
		isPending: false,
	} as never);
}

function setupBrowserMocks() {
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
		value: class {
			observe = vi.fn();
			unobserve = vi.fn();
			disconnect = vi.fn();
		},
	});
}

describe("AppUpdateSection", () => {
	beforeEach(() => {
		setupBrowserMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	describe("when the build is not configured for self-update", () => {
		beforeEach(() => {
			setupMocks("notConfigured");
		});

		it("does not offer a sign-in button that cannot work", () => {
			renderWithProviders(<AppUpdateSection />);
			expect(screen.queryByTestId("github-sign-in-card")).toBeNull();
		});

		it("does not offer a check-for-updates button, since the check is inert server-side", () => {
			renderWithProviders(<AppUpdateSection />);
			expect(screen.queryByRole("button", { name: /check for updates/i })).toBeNull();
		});

		it("explains that updates are unavailable rather than silently showing nothing", () => {
			renderWithProviders(<AppUpdateSection />);
			expect(screen.getByText(/automatic updates aren't available in this build/i)).toBeTruthy();
		});

		it("still shows the running version so the tester can report it", () => {
			renderWithProviders(<AppUpdateSection />);
			expect(screen.getByText("0.1.0-rc.2")).toBeTruthy();
		});
	});

	describe("when the build is configured", () => {
		it("offers sign-in and the check button while signed out", () => {
			setupMocks("signedOut");
			renderWithProviders(<AppUpdateSection />);

			expect(screen.getByTestId("github-sign-in-card")).toBeTruthy();
			expect(screen.getByRole("button", { name: /check for updates/i })).toBeTruthy();
			// The unconfigured notice must not leak into an ordinary signed-out build.
			expect(screen.queryByText(/automatic updates aren't available in this build/i)).toBeNull();
		});

		it("still offers sign-in when re-authentication is required", () => {
			setupMocks("reauthRequired");
			renderWithProviders(<AppUpdateSection />);

			expect(screen.getByTestId("github-sign-in-card")).toBeTruthy();
		});

		it("shows the update button when an update is available", () => {
			setupMocks("signedIn", { updateAvailable: true, availableVersion: "0.1.0-rc.3", login: "octocat" });
			renderWithProviders(<AppUpdateSection />);

			expect(screen.getByTestId("app-update-button")).toBeTruthy();
		});
	});

	it("renders nothing outside desktop mode", () => {
		setupMocks("signedOut", { isDesktop: false });
		renderWithProviders(<AppUpdateSection />);

		// Asserted on the section's own content rather than an empty container: MantineProvider injects a <style>
		// element of its own, so the container is never literally empty.
		expect(screen.queryByText(/^Updates$/)).toBeNull();
		expect(screen.queryByTestId("github-sign-in-card")).toBeNull();
		expect(screen.queryByRole("button", { name: /check for updates/i })).toBeNull();
		expect(screen.queryByText("0.1.0-rc.2")).toBeNull();
	});
});
