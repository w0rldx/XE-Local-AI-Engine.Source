// @vitest-environment jsdom

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Ending ANOTHER session refreshes the list and keeps the operator here; ending THIS browser's session (its own row, or
// sign out everywhere, which includes it) is a sign-out: the auth store is cleared and the page goes to /login.

const { confirmMock, invalidateMock, navigateMock, revokeAllMock, revokeMock, sessionsState, toastMock } = vi.hoisted(() => ({
	confirmMock: vi.fn(),
	invalidateMock: vi.fn(),
	navigateMock: vi.fn(),
	revokeAllMock: vi.fn(),
	revokeMock: vi.fn(),
	sessionsState: { data: [] as unknown[] },
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/features/node-settings/queries/useNodeAuthSessions", () => ({
	useNodeAuthSessions: () => ({ data: sessionsState.data, isLoading: false, isError: false }),
	useInvalidateNodeAuthSessions: () => invalidateMock,
	useRevokeNodeAuthSession: () => ({ mutate: revokeMock, isPending: false }),
	useRevokeAllNodeAuthSessions: () => ({ mutate: revokeAllMock, isPending: false }),
}));
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateMock,
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { NodeSessionsCard } from "@/features/node-settings/components/NodeSessionsCard";

const copy = en.pages.nodeSettings.sessions;

// Each mutate call runs its onSuccess the way TanStack Query would after a 204.
function succeed(mock: ReturnType<typeof vi.fn>): void {
	mock.mockImplementation((_variables: unknown, options: { onSuccess: () => unknown }) => options.onSuccess());
}

describe("NodeSessionsCard", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		sessionsState.data = [
			{
				id: "current",
				clientLabel: "Browser A",
				createdAtUtc: "2026-10-25T10:00:00Z",
				lastUsedAtUtc: "2026-10-25T11:00:00Z",
				current: true,
			},
			{
				id: "other",
				clientLabel: null,
				createdAtUtc: "2026-10-24T10:00:00Z",
				lastUsedAtUtc: "2026-10-24T11:00:00Z",
				current: false,
			},
		];
		useNodeAuthStore.getState().actions.setToken({ accessToken: "seeded-token", expiresAtUtc: "2999-01-01T00:00:00Z" });
	});

	it("lists every session and flags this device", () => {
		renderWithProviders(<NodeSessionsCard />);

		expect(screen.getByTestId("node-session-current").textContent).toContain("Browser A");
		expect(screen.getByTestId("node-session-current").textContent).toContain(copy.thisDevice);
		expect(screen.getByTestId("node-session-other").textContent).toContain(copy.unknownClient);
		expect(screen.getByTestId("node-session-other").textContent).not.toContain(copy.thisDevice);
	});

	it("ends another session, refreshes the list and keeps the operator signed in", async () => {
		succeed(revokeMock);
		renderWithProviders(<NodeSessionsCard />);

		fireEvent.click(screen.getByTestId("node-session-revoke-other"));

		await waitFor(() => {
			expect(invalidateMock).toHaveBeenCalled();
		});
		expect(revokeMock).toHaveBeenCalledWith({ path: { sessionId: "other" } }, expect.anything());
		expect(navigateMock).not.toHaveBeenCalled();
		expect(useNodeAuthStore.getState().accessToken).toBe("seeded-token");
	});

	it("treats ending this device's session as a sign-out", async () => {
		succeed(revokeMock);
		renderWithProviders(<NodeSessionsCard />);

		fireEvent.click(screen.getByTestId("node-session-revoke-current"));

		await waitFor(() => {
			expect(navigateMock).toHaveBeenCalledWith({ to: "/login" });
		});
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(invalidateMock).not.toHaveBeenCalled();
	});

	it("signs out everywhere only after the confirmation, then goes to sign-in", async () => {
		succeed(revokeAllMock);
		confirmMock.mockResolvedValue(true);
		renderWithProviders(<NodeSessionsCard />);

		fireEvent.click(screen.getByTestId("node-sessions-revoke-all"));

		await waitFor(() => {
			expect(navigateMock).toHaveBeenCalledWith({ to: "/login" });
		});
		expect(confirmMock).toHaveBeenCalledWith(expect.objectContaining({ title: copy.revokeAllConfirm.title }));
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
	});

	it("does nothing when sign out everywhere is cancelled", async () => {
		confirmMock.mockResolvedValue(false);
		renderWithProviders(<NodeSessionsCard />);

		fireEvent.click(screen.getByTestId("node-sessions-revoke-all"));

		await waitFor(() => {
			expect(confirmMock).toHaveBeenCalled();
		});
		expect(revokeAllMock).not.toHaveBeenCalled();
		expect(useNodeAuthStore.getState().accessToken).toBe("seeded-token");
	});
});
