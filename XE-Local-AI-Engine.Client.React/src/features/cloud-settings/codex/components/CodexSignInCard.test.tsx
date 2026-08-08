// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
	I18nextProvider: ({ children }: { children: ReactElement }) => children,
}));

// Mock the codex query hooks so tests never hit the network.
const { mockHooks } = vi.hoisted(() => ({
	mockHooks: {
		statusFn: vi.fn(),
		loginFn: vi.fn(),
		logoutFn: vi.fn(),
		loginMutate: vi.fn(),
		logoutMutate: vi.fn(),
		loginReset: vi.fn(),
	},
}));

vi.mock("@/features/cloud-settings/codex/queries/useCodexAuth", () => ({
	useCodexStatus: () => ({
		data: mockHooks.statusFn(),
		isError: false,
		error: null,
	}),
	useCodexLogin: (onAuthorizeUrl: (url: string) => void) => ({
		mutate: (args: unknown) => {
			mockHooks.loginFn(args);
			// Simulate the onSuccess callback passing back an authorizeUrl when the mock says so.
			if (mockHooks.loginMutate.mock.results[0]?.value) {
				onAuthorizeUrl(mockHooks.loginMutate.mock.results[0].value as string);
			}
		},
		isPending: false,
		isError: false,
		error: null,
		reset: mockHooks.loginReset,
	}),
	useCodexLogout: (onSuccess: () => void) => ({
		mutate: () => {
			mockHooks.logoutFn();
			onSuccess?.();
		},
		isPending: false,
	}),
}));

import { CodexSignInCard } from "@/features/cloud-settings/codex/components/CodexSignInCard";

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
}

function renderCard(onSignedInChange?: (v: boolean) => void): void {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	const ui: ReactElement = (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<CodexSignInCard onSignedInChange={onSignedInChange} />
			</MantineProvider>
		</QueryClientProvider>
	);
	render(ui);
}

describe("CodexSignInCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		// Default: signed out, no pending status.
		mockHooks.statusFn.mockReturnValue({ signedIn: false, loginPending: false });
		mockHooks.loginMutate.mockReturnValue(undefined);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders sign-in button in signed-out state", () => {
		renderCard();
		// t() returns the key in the mock; match the i18n key fragment.
		expect(screen.getByRole("button", { name: /codex\.signIn/i })).toBeTruthy();
	});

	it("does not show signed-in badge when signed out", () => {
		renderCard();
		// Badge text comes from i18n; check no account id rendered.
		expect(screen.queryByText(/accountId/i)).toBeNull();
	});

	it("shows account id and sign-out button when signed in", () => {
		mockHooks.statusFn.mockReturnValue({
			signedIn: true,
			accountId: "user-abc-123",
			expiresAtUtc: null,
		});
		renderCard();
		expect(screen.getByText("user-abc-123")).toBeTruthy();
		expect(screen.getByRole("button", { name: /codex\.signOut/i })).toBeTruthy();
	});

	it("calls onSignedInChange(true) when status transitions to signed-in", async () => {
		mockHooks.statusFn.mockReturnValue({
			signedIn: true,
			accountId: "user-abc-123",
			expiresAtUtc: null,
		});
		const spy = vi.fn();
		renderCard(spy);
		await waitFor(() => expect(spy).toHaveBeenCalledWith(true));
	});

	it("calls logout mutate when sign-out button clicked", () => {
		mockHooks.statusFn.mockReturnValue({
			signedIn: true,
			accountId: "user-abc-123",
			expiresAtUtc: null,
		});
		renderCard();
		fireEvent.click(screen.getByRole("button", { name: /codex\.signOut/i }));
		expect(mockHooks.logoutFn).toHaveBeenCalledTimes(1);
	});

	it("always renders the egress notice", () => {
		renderCard();
		// In the i18n mock t() returns the key; check the egress key is present in the DOM.
		expect(screen.getByText("pages.cloudSettings.codex.egressNotice")).toBeTruthy();
	});

	it("does not show sign-in button when signed in", () => {
		mockHooks.statusFn.mockReturnValue({
			signedIn: true,
			accountId: "user-abc-123",
			expiresAtUtc: null,
		});
		renderCard();
		expect(screen.queryByRole("button", { name: /codex\.signIn/i })).toBeNull();
	});
});
