// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { authApiMock, navigateMock, searchState } = vi.hoisted(() => ({
	authApiMock: {
		loginNodeAuth: vi.fn(),
		setupNodeAuth: vi.fn(),
	},
	navigateMock: vi.fn(),
	searchState: {
		redirect: "/dashboard",
	},
}));

vi.mock("@tanstack/react-router", () => ({
	useNavigate: () => navigateMock,
	useSearch: () => searchState,
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);

import { Login } from "@/core/auth/pages/Login";
import { Setup } from "@/core/auth/pages/Setup";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("node auth pages", () => {
	afterEach(() => {
		cleanup();
	});

	beforeEach(() => {
		vi.clearAllMocks();
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
		useNodeAuthStore.getState().actions.clear();
		authApiMock.loginNodeAuth.mockResolvedValue({ accessToken: "access-token", expiresAtUtc: "2026-05-25T12:15:00Z" });
		authApiMock.setupNodeAuth.mockResolvedValue(undefined);
	});

	it("logs in with password only and navigates to the safe redirect", async () => {
		renderWithProviders(<Login />);

		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: "correct horse" } });
		fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

		await waitFor(() => expect(authApiMock.loginNodeAuth).toHaveBeenCalledWith({ password: "correct horse" }));
		expect(useNodeAuthStore.getState().accessToken).toBe("access-token");
		expect(navigateMock).toHaveBeenCalledWith({ to: "/dashboard" });
	});

	it("sets up the first admin and immediately logs in", async () => {
		renderWithProviders(<Setup />);

		fireEvent.change(screen.getByLabelText(/^Email/), { target: { value: "admin@example.test" } });
		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: "long-enough-password" } });
		fireEvent.change(screen.getByLabelText(/^Confirm password/), { target: { value: "long-enough-password" } });
		fireEvent.click(screen.getByRole("button", { name: "Create admin" }));

		await waitFor(() =>
			expect(authApiMock.setupNodeAuth).toHaveBeenCalledWith({
				email: "admin@example.test",
				password: "long-enough-password",
			}),
		);
		expect(authApiMock.loginNodeAuth).toHaveBeenCalledWith({ email: "admin@example.test", password: "long-enough-password" });
		expect(useNodeAuthStore.getState().accessToken).toBe("access-token");
		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
	});
});
