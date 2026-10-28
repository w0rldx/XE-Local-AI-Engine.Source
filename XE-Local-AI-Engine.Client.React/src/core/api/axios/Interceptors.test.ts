// @vitest-environment jsdom

import axios, { AxiosError, AxiosHeaders, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { authApiMock, routerMock } = vi.hoisted(() => ({
	authApiMock: {
		refreshNodeAuthToken: vi.fn(),
		getNodeAuthStatus: vi.fn(),
	},
	routerMock: {
		navigate: vi.fn(),
	},
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);

import {
	addBearerReconciliationInterceptor,
	addFormDataContentTypeInterceptor,
	addUnauthorizedErrorInterceptor,
	addVaultLockedInterceptor,
} from "@/core/api/axios/Interceptors";
import { registerLoginNavigator, registerVaultNavigator } from "@/core/api/axios/LoginNavigation";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { getPendingVaultStep } from "@/core/auth/utils/SessionRestore";

registerLoginNavigator((redirect) => routerMock.navigate({ to: "/login", search: { redirect } }));
registerVaultNavigator((redirect) => routerMock.navigate({ to: "/vault", search: { redirect } }));

function okResponse(config: InternalAxiosRequestConfig): AxiosResponse {
	return {
		data: { ok: true },
		status: 200,
		statusText: "OK",
		headers: {},
		config,
	};
}

function unauthorizedError(config: InternalAxiosRequestConfig): AxiosError {
	return new AxiosError("Unauthorized", AxiosError.ERR_BAD_REQUEST, config, undefined, {
		data: undefined,
		status: 401,
		statusText: "Unauthorized",
		headers: {},
		config,
	});
}

describe("form-data content-type interceptor", () => {
	// Regression guard for the skill-import 415: the instance defaults Content-Type to application/json, and axios
	// re-serializes a FormData body to JSON whenever that header is present — silently downgrading multipart uploads
	// to a JSON body the multipart-only endpoints reject. The interceptor drops the header for FormData only.
	it("drops the JSON default content-type for a FormData body", async () => {
		let observed: unknown;
		const instance = axios.create({
			headers: { "Content-Type": "application/json" },
			adapter: async (config) => {
				observed = config.headers.get("Content-Type");
				return okResponse(config);
			},
		});
		addFormDataContentTypeInterceptor(instance);

		const body = new FormData();
		body.append("source", "GitHub");
		body.append("owner", "anthropics");
		await instance.post("/api/local/v1/skills/import/preview", body);

		expect(String(observed ?? "")).not.toContain("application/json");
	});

	it("leaves the JSON content-type intact for a plain object body", async () => {
		let observed: unknown;
		const instance = axios.create({
			headers: { "Content-Type": "application/json" },
			adapter: async (config) => {
				observed = config.headers.get("Content-Type");
				return okResponse(config);
			},
		});
		addFormDataContentTypeInterceptor(instance);

		await instance.post("/api/local/v1/skills", { name: "x" });

		expect(String(observed ?? "")).toContain("application/json");
	});
});

describe("auth axios interceptors", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
	});

	it("refreshes once and replays a 401 request with the new token", async () => {
		let callCount = 0;
		let replayAuthorization: unknown;
		const instance = axios.create({
			adapter: async (config) => {
				callCount += 1;
				if (callCount === 1) {
					throw unauthorizedError(config);
				}

				replayAuthorization = config.headers.Authorization;
				return okResponse(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		authApiMock.refreshNodeAuthToken.mockResolvedValue({ accessToken: "fresh-token", expiresAtUtc: "2026-05-25T12:15:00Z" });

		await expect(instance.get("/api/local/v1/protected")).resolves.toMatchObject({ data: { ok: true } });

		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1);
		expect(replayAuthorization).toBe("Bearer fresh-token");
		expect(useNodeAuthStore.getState().accessToken).toBe("fresh-token");
	});

	it("replays a late 401 sent with the old token using the current one, without a second refresh", async () => {
		// Two requests leave with the old token. The first 401 refreshes; the second 401 lands after that refresh has
		// settled, so the single-flight no longer covers it. Refreshing again per late 401 burns the shared auth budget.
		const sentAuthorizations: unknown[] = [];
		let releaseLateResponse!: () => void;
		const lateResponseGate = new Promise<void>((resolve) => {
			releaseLateResponse = resolve;
		});
		const instance = axios.create({
			adapter: async (config) => {
				const authorization = config.headers.Authorization;
				sentAuthorizations.push(authorization);
				if (authorization === "Bearer fresh-token") {
					return okResponse(config);
				}
				if (sentAuthorizations.length === 2) {
					await lateResponseGate;
				}
				throw unauthorizedError(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		// The generated client's `auth` callback puts the bearer on the request headers; send it the same way.
		const sentWithOldToken = { headers: { Authorization: "Bearer old-token" } };
		let releaseRefresh!: () => void;
		const freshToken = { accessToken: "fresh-token", expiresAtUtc: "2026-05-25T12:15:00Z" };
		authApiMock.refreshNodeAuthToken
			.mockImplementationOnce(
				() =>
					new Promise((resolve) => {
						releaseRefresh = () => resolve(freshToken);
					}),
			)
			.mockResolvedValue(freshToken);

		const first = instance.get("/api/local/v1/first", sentWithOldToken);
		const late = instance.get("/api/local/v1/late", sentWithOldToken);
		await vi.waitFor(() => expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1));
		expect(sentAuthorizations).toEqual(["Bearer old-token", "Bearer old-token"]);
		releaseRefresh();
		await expect(first).resolves.toMatchObject({ data: { ok: true } });

		releaseLateResponse();
		await expect(late).resolves.toMatchObject({ data: { ok: true } });

		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1);
		expect(sentAuthorizations.at(-1)).toBe("Bearer fresh-token");
	});

	it("refreshes once when the replay after a successful refresh answers 401 again", async () => {
		// axios merges a replayed config into a new object, so a retry marker keyed by object identity was lost and a
		// persistent 401 refreshed and replayed again.
		let sendCount = 0;
		const instance = axios.create({
			adapter: async (config) => {
				sendCount += 1;
				// Bound a regression: an unmarked replay loops refresh/replay without end, so stop it with a non-401.
				if (sendCount > 5) {
					throw new Error("refresh/replay loop");
				}
				throw unauthorizedError(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.refreshNodeAuthToken.mockResolvedValue({ accessToken: "fresh-token", expiresAtUtc: "2026-05-25T12:15:00Z" });

		await expect(
			instance.get("/api/local/v1/protected", { headers: { Authorization: "Bearer old-token" } }),
		).rejects.toMatchObject({ response: { status: 401 } });

		expect(authApiMock.refreshNodeAuthToken).toHaveBeenCalledTimes(1);
		expect(sendCount).toBe(2);
		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(routerMock.navigate).toHaveBeenCalledWith({ to: "/login", search: { redirect: "/" } });
	});

	it("re-reads the current token at dispatch for a request that carries a bearer, and adds none otherwise", async () => {
		const sent: unknown[] = [];
		const instance = axios.create({
			adapter: async (config) => {
				sent.push(config.headers.get("Authorization"));
				return okResponse(config);
			},
		});
		addBearerReconciliationInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "current-token", expiresAtUtc: "2026-05-25T12:00:00Z" });

		await instance.get("/api/local/v1/protected", { headers: { Authorization: "Bearer stale-token" } });
		await instance.get("/api/local/v1/protected", { headers: { authorization: "Bearer stale-token" } });
		await instance.get("/api/local/v1/auth/status");

		expect(sent).toEqual(["Bearer current-token", "Bearer current-token", undefined]);
	});

	it("clears local auth and redirects to login when refresh answers 401", async () => {
		const instance = axios.create({
			adapter: async (config) => {
				throw unauthorizedError(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		const refreshError = unauthorizedError({ headers: new AxiosHeaders() });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(refreshError);

		await expect(instance.get("/api/local/v1/protected")).rejects.toBe(refreshError);

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(routerMock.navigate).toHaveBeenCalledWith({ to: "/login", search: { redirect: "/" } });
	});

	// A rate-limited refresh is not a sign-out; the refresh cookie is still valid.
	it("keeps local auth and stays put when refresh is rate limited", async () => {
		const instance = axios.create({
			adapter: async (config) => {
				throw unauthorizedError(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		const config = { headers: new AxiosHeaders() };
		const refreshError = new AxiosError("Too Many Requests", AxiosError.ERR_BAD_REQUEST, config, undefined, {
			data: undefined,
			status: 429,
			statusText: "Too Many Requests",
			headers: {},
			config,
		});
		authApiMock.refreshNodeAuthToken.mockRejectedValue(refreshError);

		await expect(instance.get("/api/local/v1/protected")).rejects.toBe(refreshError);

		expect(useNodeAuthStore.getState().accessToken).toBe("old-token");
		expect(routerMock.navigate).not.toHaveBeenCalled();
	});
});

function serviceUnavailable(config: InternalAxiosRequestConfig, title: string): AxiosError {
	return new AxiosError("Service Unavailable", AxiosError.ERR_BAD_RESPONSE, config, undefined, {
		data: { title, status: 503 },
		status: 503,
		statusText: "Service Unavailable",
		headers: {},
		config,
	});
}

// A tab that stays open across an engine restart holds a token and a settled vault cache; the relaunched, still
// locked engine answers every protected route with 503 "Vault locked", which must send the tab to the unlock page.
describe("vault-locked interceptor", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
	});

	it("clears auth, resets the settled vault cache and navigates to the unlock page", async () => {
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: true, vault: "unlocked" });
		await getPendingVaultStep();
		await getPendingVaultStep();
		// Settled after the first unlocked read: the second call skipped the status endpoint.
		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(1);

		const instance = axios.create({
			adapter: async (config) => {
				throw serviceUnavailable(config, "Vault locked");
			},
		});
		addVaultLockedInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });

		await expect(instance.get("/api/local/v1/protected")).rejects.toBeInstanceOf(AxiosError);

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(routerMock.navigate).toHaveBeenCalledWith({ to: "/vault", search: { redirect: "/" } });
		authApiMock.getNodeAuthStatus.mockResolvedValue({ setupRequired: false, authenticated: false, vault: "locked" });
		await expect(getPendingVaultStep()).resolves.toBe("vault-locked");
		expect(authApiMock.getNodeAuthStatus).toHaveBeenCalledTimes(2);
	});

	it("hands the unlock page the path and query the tab was on", async () => {
		globalThis.history.pushState({}, "", "/chat?thread=7");
		try {
			const instance = axios.create({
				adapter: async (config) => {
					throw serviceUnavailable(config, "Vault locked");
				},
			});
			addVaultLockedInterceptor(instance);

			await expect(instance.get("/api/local/v1/protected")).rejects.toBeInstanceOf(AxiosError);

			expect(routerMock.navigate).toHaveBeenCalledWith({ to: "/vault", search: { redirect: "/chat?thread=7" } });
		} finally {
			globalThis.history.pushState({}, "", "/");
		}
	});

	it("leaves any other 503 alone", async () => {
		const instance = axios.create({
			adapter: async (config) => {
				throw serviceUnavailable(config, "Service Unavailable");
			},
		});
		addVaultLockedInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });

		await expect(instance.get("/api/local/v1/protected")).rejects.toBeInstanceOf(AxiosError);

		expect(useNodeAuthStore.getState().accessToken).toBe("old-token");
		expect(routerMock.navigate).not.toHaveBeenCalled();
	});
});
