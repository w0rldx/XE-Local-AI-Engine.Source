// @vitest-environment jsdom

import { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig, isAxiosError } from "axios";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { toastMock } = vi.hoisted(() => ({ toastMock: { error: vi.fn(), success: vi.fn(), info: vi.fn() } }));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

// The auth module is this file's entry into the import ring it shares with the generated client, as in the app.
import {
	getNodeAuthStatus,
	loginNodeAuth,
	logoutNodeAuth,
	nodeAuthClient,
	refreshNodeAuthToken,
} from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

interface SentRequest {
	url: string | undefined;
	authorization: unknown;
}

const token = { accessToken: "fresh-token", expiresAtUtc: "2026-10-15T12:00:00Z" };

// A fake transport: records what each request carried and answers with the status the test picked.
function fakeAdapter(sent: SentRequest[], status: number, data: unknown) {
	return async (config: InternalAxiosRequestConfig): Promise<AxiosResponse> => {
		sent.push({ url: config.url, authorization: config.headers.get("Authorization") });
		const response: AxiosResponse = { data, status, statusText: String(status), headers: {}, config };
		if (status >= 400) {
			throw new AxiosError(String(status), AxiosError.ERR_BAD_REQUEST, config, undefined, response);
		}
		return response;
	};
}

const authInstance = nodeAuthClient.instance;
const originalAuthAdapter = authInstance.defaults.adapter;

beforeEach(() => {
	useNodeAuthStore.getState().actions.clear();
	toastMock.error.mockClear();
});

afterEach(() => {
	authInstance.defaults.adapter = originalAuthAdapter;
});

describe("auth client", () => {
	it("leaves a failed login as one rejected request, without the refresh path", async () => {
		const sent: SentRequest[] = [];
		authInstance.defaults.adapter = fakeAdapter(sent, 401, { message: "Invalid credentials" });
		useNodeAuthStore.getState().actions.setToken(token);

		const error = await loginNodeAuth({ password: "wrong-password" }).catch((caught: unknown) => caught);

		expect(isAxiosError(error) && error.response?.status).toBe(401);
		expect(sent).toEqual([{ url: "/api/local/v1/auth/login", authorization: undefined }]);
		expect(useNodeAuthStore.getState().accessToken).toBe(token.accessToken);
	});

	it("does not toast a 429 the login page reports itself", async () => {
		const sent: SentRequest[] = [];
		authInstance.defaults.adapter = fakeAdapter(sent, 429, { message: "Too many attempts" });

		const error = await loginNodeAuth({ password: "any-password" }).catch((caught: unknown) => caught);

		expect(isAxiosError(error) && error.response?.status).toBe(429);
		expect(toastMock.error).not.toHaveBeenCalled();
		expect(sent).toHaveLength(1);
	});

	it("keeps a 400 as the raw axios error the pages read the node's message from", async () => {
		authInstance.defaults.adapter = fakeAdapter([], 400, { message: "Password too short", errors: ["min length"] });

		const error = await loginNodeAuth({ password: "short" }).catch((caught: unknown) => caught);

		expect(isAxiosError(error) && error.response?.data).toEqual({ message: "Password too short", errors: ["min length"] });
	});

	it("sends the bearer on a secured auth call and none on an unsecured one", async () => {
		const sent: SentRequest[] = [];
		authInstance.defaults.adapter = async (config) => {
			const answer = config.url?.endsWith("/auth/status")
				? { setupRequired: false, authenticated: true, vault: "unlocked" }
				: undefined;
			return fakeAdapter(sent, answer ? 200 : 204, answer)(config);
		};
		useNodeAuthStore.getState().actions.setToken(token);

		await logoutNodeAuth();
		const status = await getNodeAuthStatus();

		expect(status.vault).toBe("unlocked");
		expect(sent).toEqual([
			{ url: "/api/local/v1/auth/logout", authorization: "Bearer fresh-token" },
			{ url: "/api/local/v1/auth/status", authorization: undefined },
		]);
	});

	it("shares one refresh request between concurrent callers", async () => {
		const sent: SentRequest[] = [];
		authInstance.defaults.adapter = fakeAdapter(sent, 200, token);

		const [first, second] = await Promise.all([refreshNodeAuthToken(), refreshNodeAuthToken()]);

		expect(first).toEqual(token);
		expect(second).toEqual(token);
		expect(sent).toHaveLength(1);
	});
});
