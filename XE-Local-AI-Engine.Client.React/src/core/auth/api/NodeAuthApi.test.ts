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
	refreshRetryWaitSeconds,
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
				? { setupRequired: false, authenticated: true, vault: "unlocked", processGeneration: "generation-1" }
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

	describe("a rate-limited refresh", () => {
		// Answers each refresh from the list in order and records how many were sent.
		function scriptedRefresh(sent: SentRequest[], statuses: number[]) {
			return async (config: InternalAxiosRequestConfig): Promise<AxiosResponse> => {
				sent.push({ url: config.url, authorization: undefined });
				const status = statuses[sent.length - 1] ?? 200;
				const data = status === 200 ? token : { message: "Too many auth attempts. Please try again later." };
				const response: AxiosResponse = { data, status, statusText: String(status), headers: { "retry-after": "60" }, config };
				if (status >= 400) {
					throw new AxiosError(String(status), AxiosError.ERR_BAD_REQUEST, config, undefined, response);
				}
				return response;
			};
		}

		beforeEach(() => {
			vi.useFakeTimers();
		});

		afterEach(() => {
			vi.useRealTimers();
		});

		it("waits out Retry-After once and resolves with the retried refresh", async () => {
			const sent: SentRequest[] = [];
			authInstance.defaults.adapter = scriptedRefresh(sent, [429, 200]);

			const refreshed = refreshNodeAuthToken();
			const joined = refreshNodeAuthToken();
			await vi.advanceTimersByTimeAsync(59_000);
			expect(sent).toHaveLength(1);
			await vi.advanceTimersByTimeAsync(1_000);

			await expect(refreshed).resolves.toEqual(token);
			await expect(joined).resolves.toEqual(token);
			expect(sent).toHaveLength(2);
		});

		it("exposes the seconds left on the Retry-After wait while it is sat out, and 0 after", async () => {
			const sent: SentRequest[] = [];
			authInstance.defaults.adapter = scriptedRefresh(sent, [429, 200]);
			expect(refreshRetryWaitSeconds()).toBe(0);

			const refreshed = refreshNodeAuthToken();
			await vi.advanceTimersByTimeAsync(0);
			expect(refreshRetryWaitSeconds()).toBe(60);
			await vi.advanceTimersByTimeAsync(59_500);
			expect(refreshRetryWaitSeconds()).toBe(1);
			await vi.advanceTimersByTimeAsync(500);

			await expect(refreshed).resolves.toEqual(token);
			expect(refreshRetryWaitSeconds()).toBe(0);
		});

		it("answers later callers from the second 429 until Retry-After has passed", async () => {
			const sent: SentRequest[] = [];
			authInstance.defaults.adapter = scriptedRefresh(sent, [429, 429, 200]);

			const first = refreshNodeAuthToken().catch((caught: unknown) => caught);
			await vi.advanceTimersByTimeAsync(60_000);
			const firstError = await first;
			const blocked = await refreshNodeAuthToken().catch((caught: unknown) => caught);

			expect(isAxiosError(firstError) && firstError.response?.status).toBe(429);
			expect(blocked).toBe(firstError);
			expect(sent).toHaveLength(2);

			await vi.advanceTimersByTimeAsync(60_000);
			await expect(refreshNodeAuthToken()).resolves.toEqual(token);
			expect(sent).toHaveLength(3);
		});
	});
});
