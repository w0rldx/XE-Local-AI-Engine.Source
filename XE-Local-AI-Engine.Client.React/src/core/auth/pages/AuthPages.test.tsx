// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";

// Resolve dotted i18n key paths against the English translation JSON so assertions match real copy.
function resolveKey(key: string, options?: Record<string, unknown>): string {
	const parts = key.split(".");
	let node: unknown = en;
	for (const part of parts) {
		if (node && typeof node === "object" && part in (node as Record<string, unknown>)) {
			node = (node as Record<string, unknown>)[part];
		} else {
			return key;
		}
	}
	let text = typeof node === "string" ? node : key;
	if (options) {
		for (const [name, value] of Object.entries(options)) {
			text = text.replace(`{{${name}}}`, String(value));
		}
	}
	return text;
}

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (k: string, options?: Record<string, unknown>) => resolveKey(k, options),
		i18n: { changeLanguage: vi.fn() },
	}),
}));

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

	// The lockout 401 is the only login failure that carries a body. Without the `code` branch the operator reads
	// "Incorrect password" while holding the right one, with nothing saying that waiting is the fix.
	it("tells the operator to wait when the account is locked out", async () => {
		authApiMock.loginNodeAuth.mockRejectedValue({
			isAxiosError: true,
			response: {
				status: 401,
				data: { message: "Too many failed sign-in attempts.", code: "locked-out", retryAfterSeconds: 300 },
			},
		});
		renderWithProviders(<Login />);

		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: "correct horse" } });
		fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

		expect(await screen.findByText(resolveKey("auth.login.errorLockedOut", { minutes: 5 }))).toBeTruthy();
		expect(screen.queryByText(resolveKey("auth.login.errorIncorrectPassword"))).toBeNull();
	});

	// A wrong password before the threshold has no body, so it must still read as a wrong password.
	it("reports a plain wrong password when the 401 carries no code", async () => {
		authApiMock.loginNodeAuth.mockRejectedValue({
			isAxiosError: true,
			response: { status: 401, data: "" },
		});
		renderWithProviders(<Login />);

		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: "wrong horse" } });
		fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

		expect(await screen.findByText(resolveKey("auth.login.errorIncorrectPassword"))).toBeTruthy();
	});

	it("sets up the first admin and immediately logs in", async () => {
		renderWithProviders(<Setup />);

		// Password satisfies the client-side policy mirrored from ASP.NET Identity:
		// 12+ chars, upper, lower, digit, and a symbol.
		const password = "Long-Enough-Password1";
		fireEvent.change(screen.getByLabelText(/^Email/), { target: { value: "admin@example.test" } });
		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: password } });
		fireEvent.change(screen.getByLabelText(/^Confirm password/), { target: { value: password } });
		fireEvent.click(screen.getByRole("button", { name: "Create admin" }));

		await waitFor(() =>
			expect(authApiMock.setupNodeAuth).toHaveBeenCalledWith({
				email: "admin@example.test",
				password,
			}),
		);
		expect(authApiMock.loginNodeAuth).toHaveBeenCalledWith({ email: "admin@example.test", password });
		expect(useNodeAuthStore.getState().accessToken).toBe("access-token");
		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
	});

	it("blocks setup and surfaces the policy when the password is too weak", async () => {
		renderWithProviders(<Setup />);

		// Missing an uppercase letter and a digit, so the client-side policy must reject it.
		const weakPassword = "weak-password";
		fireEvent.change(screen.getByLabelText(/^Email/), { target: { value: "admin@example.test" } });
		fireEvent.change(screen.getByLabelText(/^Password/), { target: { value: weakPassword } });
		fireEvent.change(screen.getByLabelText(/^Confirm password/), { target: { value: weakPassword } });
		fireEvent.click(screen.getByRole("button", { name: "Create admin" }));

		expect(await screen.findByText(/Password needs/)).toBeTruthy();
		expect(authApiMock.setupNodeAuth).not.toHaveBeenCalled();
	});

	// The literal first screen of a fresh install. `errors` is recomputed on every render including the first, so
	// without a touched gate the operator was greeted by two red validation errors for fields they had not typed in.
	it("shows no validation errors before the operator has touched a field", () => {
		renderWithProviders(<Setup />);

		expect(screen.queryByText(resolveKey("auth.setup.validationEmail"))).toBeNull();
		expect(screen.queryByText(resolveKey("auth.setup.validationPasswordRequired"))).toBeNull();
		// Still not submittable — the gate hides the message, it does not weaken the check.
		expect((screen.getByRole("button", { name: "Create admin" }) as HTMLButtonElement).disabled).toBe(true);
	});

	it("shows a field's validation error once that field has been touched", () => {
		renderWithProviders(<Setup />);

		fireEvent.change(screen.getByLabelText(/^Email/), { target: { value: "not-an-email" } });

		expect(screen.getByText(resolveKey("auth.setup.validationEmail"))).toBeTruthy();
		// A field the operator has still not touched stays quiet.
		expect(screen.queryByText(resolveKey("auth.setup.validationPasswordRequired"))).toBeNull();
	});
});
