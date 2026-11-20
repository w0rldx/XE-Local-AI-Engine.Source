// @vitest-environment jsdom

import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const navigate = vi.hoisted(() => vi.fn(async () => undefined));
const revoke = vi.hoisted(() => vi.fn(async () => undefined));
vi.mock("@tanstack/react-router", () => ({ useNavigate: () => navigate }));
vi.mock("@/core/auth/api/NodeAuthApi", () => ({ logoutNodeAuth: revoke }));

import { useNodeLogout } from "@/core/auth/hooks/useNodeLogout";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { alertedWaits, recordAlertedWaits } from "@/core/operations/models/JobAlerts";

afterEach(() => {
	globalThis.localStorage.clear();
	useNodeAuthStore.setState({ accessToken: undefined });
});

describe("useNodeLogout", () => {
	it("forgets the waits this browser alerted, with the session, even when the revoke fails", async () => {
		useNodeAuthStore.setState({ accessToken: "test-access-token" });
		recordAlertedWaits([], ["graph-run:run-9"], [], new Set(["graph-run:run-9"]));
		revoke.mockRejectedValueOnce(new Error("offline"));
		const { result } = renderHook(() => useNodeLogout());

		await act(() => result.current.logout().catch(() => undefined));

		expect(alertedWaits()).toEqual([]);
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(navigate).toHaveBeenCalledWith({ to: "/login" });
	});
});
