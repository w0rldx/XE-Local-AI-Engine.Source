// @vitest-environment jsdom

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { AxiosError, type AxiosResponse } from "axios";
import { beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The card is the ONLY way an operator can change the node password, and a success is a sign-out: the node rotates the
// security stamp, revokes every refresh token and answers 204 with no replacement pair, so the session the operator is
// sitting in is already dead. What is pinned here is exactly that — the warning is on screen before anything is typed,
// a success clears the auth store and routes to /login, and a REJECTED change leaves the operator signed in with the
// node's own reason on screen (a card that cleared the store on a wrong-password 400 would log them out for nothing).

const { authApiMock, navigateMock, toastMock } = vi.hoisted(() => ({
	authApiMock: { changeNodePassword: vi.fn() },
	navigateMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
// Partial mock: renderWithProviders builds a real memory router for other suites, so only the navigate hook is
// replaced. The card is rendered without router context, which is why the hook cannot stay real.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateMock,
}));

import { NodeChangePasswordCard } from "@/features/node-settings/components/NodeChangePasswordCard";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

const copy = en.pages.nodeSettings.changePassword;
const currentPassword = "Str0ng!Password123";
const newPassword = "R3placed!Password456";

// Addressed by test id rather than by label: Mantine's PasswordInput labels the OUTER wrapper, so getByLabelText
// finds no form control. The label copy is still asserted — the inputs carry it as their rendered label text.
function fill(testId: string, value: string): void {
	fireEvent.change(screen.getByTestId(testId), { target: { value } });
}

function fillValidForm(): void {
	fill("node-change-password-current", currentPassword);
	fill("node-change-password-new", newPassword);
	fill("node-change-password-confirm", newPassword);
}

function submitButton(): HTMLButtonElement {
	return screen.getByTestId("node-change-password-submit") as HTMLButtonElement;
}

function axiosFailure(status: number, data: unknown): AxiosError {
	const response = {
		status,
		statusText: "",
		data,
		headers: {},
		config: { headers: {} },
	} as unknown as AxiosResponse;

	return new AxiosError(`Request failed with status code ${status}`, "ERR_BAD_REQUEST", undefined, undefined, response);
}

function badRequest(errors: string[]): AxiosError {
	return axiosFailure(400, { message: "Password change failed.", errors });
}

describe("NodeChangePasswordCard", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.setToken({ accessToken: "seeded-token", expiresAtUtc: "2999-01-01T00:00:00Z" });
	});

	it("warns up front that a successful change signs the operator out", () => {
		renderWithProviders(<NodeChangePasswordCard />);

		expect(screen.getByTestId("node-change-password-description").textContent).toBe(copy.description);
		// Disabled before anything is typed: the empty form is invalid, so there is nothing to submit yet.
		expect(submitButton().disabled).toBe(true);
	});

	it("rejects a new password that fails the node's policy", () => {
		renderWithProviders(<NodeChangePasswordCard />);

		fill("node-change-password-current", currentPassword);
		fill("node-change-password-new", "short");

		expect(screen.getByText(copy.validationNewWeak)).toBeTruthy();
		expect(submitButton().disabled).toBe(true);
	});

	it("rejects a new password identical to the current one", () => {
		renderWithProviders(<NodeChangePasswordCard />);

		fill("node-change-password-current", currentPassword);
		fill("node-change-password-new", currentPassword);
		fill("node-change-password-confirm", currentPassword);

		expect(screen.getByText(copy.validationNewSameAsCurrent)).toBeTruthy();
		expect(submitButton().disabled).toBe(true);
	});

	it("rejects a confirmation that does not match the new password", () => {
		renderWithProviders(<NodeChangePasswordCard />);

		fillValidForm();
		fill("node-change-password-confirm", `${newPassword}-typo`);

		expect(screen.getByText(copy.validationNoMatch)).toBeTruthy();
		expect(submitButton().disabled).toBe(true);
	});

	it("sends the change, then clears the session and routes to the sign-in page", async () => {
		authApiMock.changeNodePassword.mockResolvedValue(undefined);
		renderWithProviders(<NodeChangePasswordCard />);

		fillValidForm();
		fireEvent.click(submitButton());

		await waitFor(() => {
			expect(navigateMock).toHaveBeenCalledWith({ to: "/login" });
		});
		expect(authApiMock.changeNodePassword).toHaveBeenCalledWith({ currentPassword, newPassword });
		expect(toastMock.success).toHaveBeenCalledWith(copy.success);
		// The node already killed this session; a client still holding the token would only discover it on the next 401.
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
	});

	it("shows the node's rejection reason and keeps the operator signed in", async () => {
		authApiMock.changeNodePassword.mockRejectedValue(badRequest(["Incorrect password."]));
		renderWithProviders(<NodeChangePasswordCard />);

		fillValidForm();
		fireEvent.click(submitButton());

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalledWith("Incorrect password.");
		});
		expect(navigateMock).not.toHaveBeenCalled();
		expect(useNodeAuthStore.getState().accessToken).toBe("seeded-token");
	});

	// The endpoint shares the auth throttle, and the dedicated authClient carries none of the shared instance's
	// interceptors — so the card owns the 429 message itself. It must be the localized one, not the node's own English
	// sentence echoed back through apiErrorMessage.
	it("shows the localized throttle message when the node rate-limits the attempt", async () => {
		authApiMock.changeNodePassword.mockRejectedValue(
			axiosFailure(429, { message: "Too many auth attempts. Please try again later." }),
		);
		renderWithProviders(<NodeChangePasswordCard />);

		fillValidForm();
		fireEvent.click(submitButton());

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalledWith(en.errorMessages.tooManyRequests);
		});
		expect(navigateMock).not.toHaveBeenCalled();
		expect(useNodeAuthStore.getState().accessToken).toBe("seeded-token");
	});
});
