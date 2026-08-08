// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string) => key,
	}),
}));

// Mock the entra query hooks so tests never hit the network.
const { mockHooks } = vi.hoisted(() => ({
	mockHooks: {
		statusFn: vi.fn(),
		signInFn: vi.fn(),
		signInResult: undefined as { userCode: string; verificationUri: string; expiresAtUtc: string } | undefined,
		signInIsError: false,
		signInReset: vi.fn(),
	},
}));

vi.mock("@/features/cloud-settings/entra/queries/useEntraDeviceCodeAuth", () => ({
	useEntraDeviceCodeStatus: () => ({
		data: mockHooks.statusFn(),
		isError: false,
	}),
	useEntraDeviceCodeSignIn: (onStarted: (userCode: string, verificationUri: string, expiresAtUtc: string) => void) => ({
		mutate: () => {
			mockHooks.signInFn();
			if (mockHooks.signInResult) {
				onStarted(mockHooks.signInResult.userCode, mockHooks.signInResult.verificationUri, mockHooks.signInResult.expiresAtUtc);
			}
		},
		isPending: false,
		isError: mockHooks.signInIsError,
		reset: mockHooks.signInReset,
	}),
}));

const { toastMock } = vi.hoisted(() => ({ toastMock: { success: vi.fn(), error: vi.fn() } }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { EntraDeviceCodeSignInCard } from "@/features/cloud-settings/entra/components/EntraDeviceCodeSignInCard";

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

// A fresh element built for every render/rerender call — a new QueryClient per call is fine since the
// QueryClientProvider itself never unmounts, so EntraDeviceCodeSignInCard's local state survives a rerender.
function buildUi(): ReactElement {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<EntraDeviceCodeSignInCard />
			</MantineProvider>
		</QueryClientProvider>
	);
}

function renderCard() {
	return render(buildUi());
}

describe("EntraDeviceCodeSignInCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		mockHooks.statusFn.mockReturnValue(undefined);
		mockHooks.signInResult = undefined;
		mockHooks.signInIsError = false;
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the sign-in button before a sign-in has started", () => {
		renderCard();
		expect(screen.getByRole("button", { name: "pages.cloudSettings.entra.signIn" })).toBeTruthy();
	});

	it("shows the device code and verification link after starting sign-in", () => {
		mockHooks.signInResult = {
			userCode: "ABCD-1234",
			verificationUri: "https://microsoft.com/devicelogin",
			expiresAtUtc: new Date(Date.now() + 10 * 60 * 1_000).toISOString(),
		};
		renderCard();

		fireEvent.click(screen.getByRole("button", { name: "pages.cloudSettings.entra.signIn" }));

		expect(screen.getByDisplayValue("ABCD-1234")).toBeTruthy();
		expect(screen.getByDisplayValue("https://microsoft.com/devicelogin")).toBeTruthy();
		// The sign-in button is replaced by the pending state.
		expect(screen.queryByRole("button", { name: "pages.cloudSettings.entra.signIn" })).toBeNull();
	});

	it("surfaces a success toast once the poll observes the Succeeded state", async () => {
		mockHooks.signInResult = {
			userCode: "ABCD-1234",
			verificationUri: "https://microsoft.com/devicelogin",
			expiresAtUtc: new Date(Date.now() + 10 * 60 * 1_000).toISOString(),
		};
		const { rerender } = renderCard();
		fireEvent.click(screen.getByRole("button", { name: "pages.cloudSettings.entra.signIn" }));

		// Simulate the next poll tick observing the terminal state (the mocked hook re-reads statusFn() on render).
		mockHooks.statusFn.mockReturnValue({ state: "Succeeded" });
		rerender(buildUi());

		await waitFor(() => expect(toastMock.success).toHaveBeenCalled());
	});

	it("surfaces a failure toast once the poll observes the Failed state", async () => {
		mockHooks.signInResult = {
			userCode: "ABCD-1234",
			verificationUri: "https://microsoft.com/devicelogin",
			expiresAtUtc: new Date(Date.now() + 10 * 60 * 1_000).toISOString(),
		};
		const { rerender } = renderCard();
		fireEvent.click(screen.getByRole("button", { name: "pages.cloudSettings.entra.signIn" }));

		mockHooks.statusFn.mockReturnValue({ state: "Failed" });
		rerender(buildUi());

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
	});
});
