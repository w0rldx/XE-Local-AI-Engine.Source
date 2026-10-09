// @vitest-environment jsdom

import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const { authApiMock, navigateMock } = vi.hoisted(() => ({
	authApiMock: {
		getNodeAuthStatus: vi.fn(),
		unlockNodeVault: vi.fn(),
		unlockNodeVaultWithRecovery: vi.fn(),
		confirmNodeVault: vi.fn(),
	},
	// Resolves like the router's navigate, which the pages chain a `.catch` onto.
	navigateMock: vi.fn(async () => undefined),
}));

vi.mock("@tanstack/react-router", () => ({ useNavigate: () => navigateMock }));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);

import { RecoveryCodeReveal } from "@/core/auth/components/RecoveryCodeReveal";
import { VaultSetup } from "@/core/auth/pages/VaultSetup";
import { VaultUnlock } from "@/core/auth/pages/VaultUnlock";

const vault = en.auth.vault;
const strongPassword = "Long-Enough-Password1";

function axiosFailure(status: number, data: unknown = "") {
	return { isAxiosError: true, response: { status, data } };
}

// Flushes the submit's promise chain and then lets `ms` of fake time pass, all inside act so React commits.
async function advance(ms: number): Promise<void> {
	await act(async () => {
		await vi.advanceTimersByTimeAsync(ms);
	});
}

function submitButton(name: string): HTMLButtonElement {
	return screen.getByRole("button", { name }) as HTMLButtonElement;
}

describe("vault unlock page", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		vi.useFakeTimers();
		installJsdomEnvironmentMocks();
		authApiMock.unlockNodeVault.mockResolvedValue(undefined);
		authApiMock.unlockNodeVaultWithRecovery.mockResolvedValue({ recoveryCode: "NEWAA-NEWBB-NEWCC" });
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
	});

	it("unlocks, waits out the host hand-over and then navigates home", async () => {
		authApiMock.getNodeAuthStatus
			.mockRejectedValueOnce(axiosFailure(503))
			.mockRejectedValueOnce({ isAxiosError: true })
			.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "unlocked" });
		renderWithMantine(<VaultUnlock />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.passwordLabel}`)), { target: { value: "correct horse" } });
		fireEvent.click(submitButton(vault.unlockButton));
		await advance(0);

		expect(authApiMock.unlockNodeVault).toHaveBeenCalledWith({ password: "correct horse" });
		expect(screen.getByText(vault.startingEngine)).toBeTruthy();
		expect(navigateMock).not.toHaveBeenCalled();

		// Backoff 500 ms, 1 s, 2 s: the third poll is the first one the real host answers.
		await advance(1_500);
		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(2);
		expect(navigateMock).not.toHaveBeenCalled();

		await advance(2_000);
		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(3);
		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
	});

	it("offers Try again once the hand-over budget runs out, and polls again", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "locked" });
		renderWithMantine(<VaultUnlock />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.passwordLabel}`)), { target: { value: "correct horse" } });
		fireEvent.click(submitButton(vault.unlockButton));
		// 500 + 1 000 + 2 000 x 30 ms of backoff: the last wait starts just under the 60 s budget.
		await advance(61_500);

		expect(screen.getByText(vault.errorHandoverTimeout)).toBeTruthy();
		expect(navigateMock).not.toHaveBeenCalled();

		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "unlocked" });
		fireEvent.click(screen.getByRole("button", { name: vault.tryAgainButton }));
		await advance(500);

		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
		// The unlock itself already succeeded; Try again only waits again.
		expect(authApiMock.unlockNodeVault).toHaveBeenCalledTimes(1);
	});

	it("reports a wrong password and stays on the form", async () => {
		authApiMock.unlockNodeVault.mockRejectedValue(axiosFailure(401));
		renderWithMantine(<VaultUnlock />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.passwordLabel}`)), { target: { value: "wrong horse" } });
		fireEvent.click(submitButton(vault.unlockButton));
		await advance(0);

		expect(screen.getByText(vault.errorIncorrectPassword)).toBeTruthy();
		expect(screen.queryByText(vault.startingEngine)).toBeNull();
		expect(authApiMock.getNodeAuthStatus).not.toHaveBeenCalled();
	});

	it("tells the operator to wait when rate-limited", async () => {
		authApiMock.unlockNodeVault.mockRejectedValue(axiosFailure(429));
		renderWithMantine(<VaultUnlock />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.passwordLabel}`)), { target: { value: "wrong horse" } });
		fireEvent.click(submitButton(vault.unlockButton));
		await advance(0);

		expect(screen.getByText(vault.errorRateLimited)).toBeTruthy();
		expect(screen.queryByText(vault.errorIncorrectPassword)).toBeNull();
	});

	it("unlocks with the recovery code and shows the rotated code only once the real host answers", async () => {
		authApiMock.getNodeAuthStatus
			.mockRejectedValueOnce(axiosFailure(503))
			.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "unlocked" });
		renderWithMantine(<VaultUnlock />);

		fireEvent.click(screen.getByRole("button", { name: vault.useRecoveryToggle }));
		expect(submitButton(vault.recoveryUnlockButton).disabled).toBe(true);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.recoveryCodeLabel}`)), {
			target: { value: "  ABCDE-FGHIJ  " },
		});
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.newPasswordLabel}`)), { target: { value: strongPassword } });
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.confirmPasswordLabel}`)), {
			target: { value: strongPassword },
		});
		fireEvent.click(submitButton(vault.recoveryUnlockButton));
		await advance(500);

		expect(authApiMock.unlockNodeVaultWithRecovery).toHaveBeenCalledWith({
			recoveryCode: "ABCDE-FGHIJ",
			newPassword: strongPassword,
		});
		expect(authApiMock.unlockNodeVault).not.toHaveBeenCalled();
		// The pre-host answered before the real host committed the reset: nothing about rotation shows yet.
		expect(screen.getByText(vault.startingEngine)).toBeTruthy();
		expect(screen.queryByText(vault.recoveryRotated)).toBeNull();
		expect(screen.queryByTestId("recovery-code-value")).toBeNull();

		await advance(1_000);
		expect(screen.getByText(vault.recoveryRotated)).toBeTruthy();
		expect(screen.getByTestId("recovery-code-value").textContent).toBe("NEWAA-NEWBB-NEWCC");
		expect(navigateMock).not.toHaveBeenCalled();
		expect(submitButton(vault.continueButton).disabled).toBe(true);

		fireEvent.click(screen.getByLabelText(vault.recoverySavedLabel));
		fireEvent.click(submitButton(vault.continueButton));
		await advance(0);

		expect(screen.queryByTestId("recovery-code-value")).toBeNull();
		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(2);
	});

	it("never shows the rotated code when the real host does not take over", async () => {
		authApiMock.getNodeAuthStatus.mockRejectedValue({ isAxiosError: true });
		renderWithMantine(<VaultUnlock />);

		fireEvent.click(screen.getByRole("button", { name: vault.useRecoveryToggle }));
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.recoveryCodeLabel}`)), { target: { value: "ABCDE-FGHIJ" } });
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.newPasswordLabel}`)), { target: { value: strongPassword } });
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.confirmPasswordLabel}`)), {
			target: { value: strongPassword },
		});
		fireEvent.click(submitButton(vault.recoveryUnlockButton));
		await advance(61_500);

		expect(screen.getByText(vault.errorHandoverTimeout)).toBeTruthy();
		expect(screen.queryByText(vault.recoveryRotated)).toBeNull();
		expect(screen.queryByTestId("recovery-code-value")).toBeNull();
		expect(navigateMock).not.toHaveBeenCalled();
	});

	it("blocks a weak new password and reports an invalid recovery code", async () => {
		authApiMock.unlockNodeVaultWithRecovery.mockRejectedValue(axiosFailure(401));
		renderWithMantine(<VaultUnlock />);

		fireEvent.click(screen.getByRole("button", { name: vault.useRecoveryToggle }));
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.recoveryCodeLabel}`)), { target: { value: "WRONG" } });
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.newPasswordLabel}`)), { target: { value: "weak-password" } });

		expect(screen.getByText(/Password needs/)).toBeTruthy();
		expect(submitButton(vault.recoveryUnlockButton).disabled).toBe(true);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.newPasswordLabel}`)), { target: { value: strongPassword } });
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.confirmPasswordLabel}`)), {
			target: { value: strongPassword },
		});
		fireEvent.click(submitButton(vault.recoveryUnlockButton));
		await advance(0);

		expect(screen.getByText(vault.errorInvalidRecoveryCode)).toBeTruthy();
		expect(navigateMock).not.toHaveBeenCalled();
	});
});

describe("vault setup page", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("confirms the password, reveals the recovery code and continues only once it is saved", async () => {
		authApiMock.confirmNodeVault.mockResolvedValue({ recoveryCode: "AAAAA-BBBBB-CCCCC" });
		renderWithMantine(<VaultSetup />);

		expect(screen.getByText(vault.setupExplanation)).toBeTruthy();
		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.currentPasswordLabel}`)), {
			target: { value: strongPassword },
		});
		fireEvent.click(submitButton(vault.setupButton));

		expect(await screen.findByText("AAAAA-BBBBB-CCCCC")).toBeTruthy();
		expect(authApiMock.confirmNodeVault).toHaveBeenCalledWith({ password: strongPassword });
		const continueButton = submitButton(vault.continueButton);
		expect(continueButton.disabled).toBe(true);

		fireEvent.click(screen.getByLabelText(vault.recoverySavedLabel));
		expect(continueButton.disabled).toBe(false);
		fireEvent.click(continueButton);

		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
	});

	it("shows the node's message for a wrong password", async () => {
		authApiMock.confirmNodeVault.mockRejectedValue(axiosFailure(400, { message: "Invalid", errors: ["Incorrect password."] }));
		renderWithMantine(<VaultSetup />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.currentPasswordLabel}`)), { target: { value: "wrong" } });
		fireEvent.click(submitButton(vault.setupButton));

		expect(await screen.findByText("Incorrect password.")).toBeTruthy();
		expect(screen.queryByText(vault.continueButton)).toBeNull();
	});

	it("says so when the vault is already protected", async () => {
		authApiMock.confirmNodeVault.mockRejectedValue(axiosFailure(409));
		renderWithMantine(<VaultSetup />);

		fireEvent.change(screen.getByLabelText(new RegExp(`^${vault.currentPasswordLabel}`)), {
			target: { value: strongPassword },
		});
		fireEvent.click(submitButton(vault.setupButton));

		expect(await screen.findByText(vault.errorAlreadyProtected)).toBeTruthy();
	});
});

describe("recovery code reveal", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("copies the code and gates Continue on the saved checkbox", async () => {
		const writeText = vi.fn(async () => undefined);
		Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
		const onContinue = vi.fn();
		renderWithMantine(<RecoveryCodeReveal recoveryCode="AAAAA-BBBBB" onContinue={onContinue} />);

		expect(screen.getByText(vault.recoveryRevealWarning)).toBeTruthy();
		fireEvent.click(screen.getByRole("button", { name: en.common.copy }));
		expect(writeText).toHaveBeenCalledWith("AAAAA-BBBBB");
		await waitFor(() => expect(screen.getByRole("button", { name: en.common.copied })).toBeTruthy());

		fireEvent.click(submitButton(vault.continueButton));
		expect(onContinue).not.toHaveBeenCalled();

		fireEvent.click(screen.getByLabelText(vault.recoverySavedLabel));
		fireEvent.click(submitButton(vault.continueButton));
		expect(onContinue).toHaveBeenCalledTimes(1);
	});
});
