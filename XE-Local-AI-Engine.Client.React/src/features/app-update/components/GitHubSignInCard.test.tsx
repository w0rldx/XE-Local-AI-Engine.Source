// @vitest-environment jsdom

import "@/i18n";

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the query hooks so the component test is isolated from the network.
vi.mock("@/features/app-update/queries/useAppUpdate", () => ({
	noBodyOptions: {},
	useStartGitHubAuth: vi.fn(),
	usePollGitHubAuth: vi.fn(),
	useSignOutGitHubAuth: vi.fn(),
}));

import {
	usePollGitHubAuth,
	useSignOutGitHubAuth,
	useStartGitHubAuth,
} from "@/features/app-update/queries/useAppUpdate";
import { GitHubSignInCard } from "./GitHubSignInCard";

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

const mockStartMutateAsync = vi.fn();
const mockPollMutateAsync = vi.fn();
const mockSignOutMutate = vi.fn();

function setupMocks() {
	vi.mocked(useStartGitHubAuth).mockReturnValue({
		mutateAsync: mockStartMutateAsync,
		mutate: vi.fn(),
		isPending: false,
	} as never);

	vi.mocked(usePollGitHubAuth).mockReturnValue({
		mutateAsync: mockPollMutateAsync,
		mutate: vi.fn(),
		isPending: false,
	} as never);

	vi.mocked(useSignOutGitHubAuth).mockReturnValue({
		mutateAsync: vi.fn(),
		mutate: mockSignOutMutate,
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

describe("GitHubSignInCard", () => {
	beforeEach(() => {
		setupMocks();
		setupBrowserMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
		vi.useRealTimers();
	});

	it("shows sign-in button in idle state", () => {
		renderWithProviders(<GitHubSignInCard />);
		expect(screen.getByRole("button", { name: /sign in with github/i })).toBeTruthy();
	});

	it("shows privacy notice in idle state", () => {
		renderWithProviders(<GitHubSignInCard />);
		// Privacy notice must be visible before the user signs in (plan §10 L2).
		expect(screen.getByText(/contacts GitHub and identifies you/i)).toBeTruthy();
	});

	it("calls startGitHubAuth mutateAsync when sign-in button is clicked", async () => {
		// Long interval so no poll fires automatically during this assertion.
		mockStartMutateAsync.mockResolvedValue({
			userCode: "CLICK-TEST",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 3600,
		});
		mockPollMutateAsync.mockResolvedValue({ state: "pending" });

		renderWithProviders(<GitHubSignInCard />);
		fireEvent.click(screen.getByRole("button", { name: /sign in with github/i }));

		await waitFor(() => expect(mockStartMutateAsync).toHaveBeenCalledOnce());
	});

	it("shows user code after start succeeds", async () => {
		mockStartMutateAsync.mockResolvedValue({
			userCode: "SHOW-CODE",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 3600,
		});
		mockPollMutateAsync.mockResolvedValue({ state: "pending" });

		renderWithProviders(<GitHubSignInCard />);
		fireEvent.click(screen.getByRole("button", { name: /sign in with github/i }));

		await waitFor(() => expect(screen.queryByText("SHOW-CODE")).toBeTruthy());
	});

	it("polls at the returned interval after startGitHubAuth succeeds", async () => {
		// Strategy: use fake timers so we control when the poll fires, but restore real
		// timers for the act() call that flushes React state after we advance the clock.
		vi.useFakeTimers();

		mockPollMutateAsync.mockResolvedValue({ state: "pending" });
		mockStartMutateAsync.mockResolvedValue({
			userCode: "POLL-CODE",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 5,
		});

		renderWithProviders(<GitHubSignInCard />);

		// Click inside act so the start mutation resolves and the timer is registered.
		await act(async () => {
			fireEvent.click(screen.getByRole("button", { name: /sign in with github/i }));
		});

		// User code must be visible — start resolved and state updated.
		expect(screen.queryByText("POLL-CODE")).toBeTruthy();

		// Advance clock past the 5 s poll interval; act() flushes the async callback.
		await act(async () => {
			await vi.advanceTimersByTimeAsync(5001);
		});

		expect(mockPollMutateAsync).toHaveBeenCalledTimes(1);

		vi.useRealTimers();
	});

	it("stops polling and calls onAuthorized when poll returns authorized", async () => {
		vi.useFakeTimers();

		const onAuthorized = vi.fn();
		mockPollMutateAsync.mockResolvedValue({ state: "authorized", login: "octocat" });
		mockStartMutateAsync.mockResolvedValue({
			userCode: "AUTH-CODE",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 5,
		});

		renderWithProviders(<GitHubSignInCard onAuthorized={onAuthorized} />);

		await act(async () => {
			fireEvent.click(screen.getByRole("button", { name: /sign in with github/i }));
		});

		expect(screen.queryByText("AUTH-CODE")).toBeTruthy();

		await act(async () => {
			await vi.advanceTimersByTimeAsync(5001);
		});

		expect(onAuthorized).toHaveBeenCalledOnce();

		vi.useRealTimers();
	});

	it("hides the sign-in button once authorized", async () => {
		vi.useFakeTimers();

		mockPollMutateAsync.mockResolvedValue({ state: "authorized" });
		mockStartMutateAsync.mockResolvedValue({
			userCode: "HIDE-ME",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 5,
		});

		renderWithProviders(<GitHubSignInCard />);

		await act(async () => {
			fireEvent.click(screen.getByRole("button", { name: /sign in with github/i }));
		});

		expect(screen.queryByText("HIDE-ME")).toBeTruthy();

		await act(async () => {
			await vi.advanceTimersByTimeAsync(5001);
		});

		// Sign-in button must be gone once authorized.
		expect(screen.queryByRole("button", { name: /sign in with github/i })).toBeNull();

		vi.useRealTimers();
	});
});
