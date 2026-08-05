// @vitest-environment jsdom

import axios, { AxiosError, type AxiosResponse, type InternalAxiosRequestConfig } from "axios";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { authApiMock, routerMock } = vi.hoisted(() => ({
	authApiMock: {
		refreshNodeAuthToken: vi.fn(),
	},
	routerMock: {
		navigate: vi.fn(),
	},
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => authApiMock);
vi.mock("@/core/integrations/tanstack-router/Router", () => ({ router: routerMock }));

import {
	addAuthRequestInterceptor,
	addFormDataContentTypeInterceptor,
	addUnauthorizedErrorInterceptor,
} from "@/core/api/axios/Interceptors";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

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
	return new AxiosError(
		"Unauthorized",
		AxiosError.ERR_BAD_REQUEST,
		config,
		undefined,
		{
			data: undefined,
			status: 401,
			statusText: "Unauthorized",
			headers: {},
			config,
		},
	);
}

describe("form-data content-type interceptor", () => {
	// Regression guard for the skill-import 415: the instance defaults Content-Type to application/json, and axios
	// re-serialises a FormData body to JSON whenever that header is present — silently downgrading multipart uploads
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

	it("adds the in-memory access token as a bearer header", async () => {
		let observedAuthorization: unknown;
		const instance = axios.create({
			adapter: async (config) => {
				observedAuthorization = config.headers.Authorization;
				return okResponse(config);
			},
		});
		addAuthRequestInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2026-05-25T12:00:00Z" });

		await instance.get("/api/local/v1/protected");

		expect(observedAuthorization).toBe("Bearer access-token");
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

	it("clears local auth and redirects to login when refresh fails", async () => {
		const instance = axios.create({
			adapter: async (config) => {
				throw unauthorizedError(config);
			},
		});
		addUnauthorizedErrorInterceptor(instance);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		authApiMock.refreshNodeAuthToken.mockRejectedValue(new Error("expired"));

		await expect(instance.get("/api/local/v1/protected")).rejects.toThrow("expired");

		expect(useNodeAuthStore.getState().accessToken).toBeUndefined();
		expect(routerMock.navigate).toHaveBeenCalledWith({ to: "/login", search: { redirect: "/" } });
	});
});
