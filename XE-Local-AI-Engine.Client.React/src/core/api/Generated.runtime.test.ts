// @vitest-environment jsdom

import { AxiosError, type AxiosResponse } from "axios";
import { afterEach, describe, expect, it } from "vitest";

import { client } from "@/core/api/generated/client.gen";
import { getNodeSettings } from "@/core/api/generated/sdk.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

const originalAdapter = client.instance.defaults.adapter;

afterEach(() => {
	client.instance.defaults.adapter = originalAdapter;
	useNodeAuthStore.getState().actions.clear();
});

describe("shared client bearer", () => {
	// The hey-api `auth` callback is the only bearer source: it reads the store per request, so each call carries
	// exactly one Authorization header with the current token.
	it("sends exactly one current bearer per request from the auth callback", async () => {
		const sent: unknown[] = [];
		client.instance.defaults.adapter = async (config): Promise<AxiosResponse> => {
			sent.push(config.headers.get("Authorization"));
			const response: AxiosResponse = { data: {}, status: 500, statusText: "500", headers: {}, config };
			throw new AxiosError("500", AxiosError.ERR_BAD_RESPONSE, config, undefined, response);
		};

		useNodeAuthStore.getState().actions.setToken({ accessToken: "first-token", expiresAtUtc: "2026-10-15T12:00:00Z" });
		await getNodeSettings().catch(() => undefined);
		useNodeAuthStore.getState().actions.setToken({ accessToken: "second-token", expiresAtUtc: "2026-10-15T12:00:00Z" });
		await getNodeSettings().catch(() => undefined);

		expect(sent).toEqual(["Bearer first-token", "Bearer second-token"]);
	});

	it("sends the token rotated after the auth callback resolved but before dispatch", async () => {
		const sent: unknown[] = [];
		client.instance.defaults.adapter = async (config): Promise<AxiosResponse> => {
			sent.push(config.headers.get("Authorization"));
			const response: AxiosResponse = { data: {}, status: 500, statusText: "500", headers: {}, config };
			throw new AxiosError("500", AxiosError.ERR_BAD_RESPONSE, config, undefined, response);
		};
		const sentBeforeRotation: unknown[] = [];
		// axios runs request interceptors last-registered first, so this one sees the request hey-api already
		// stamped with the old bearer and rotates the store before the app's own interceptors run.
		const rotate = client.instance.interceptors.request.use((config) => {
			sentBeforeRotation.push(config.headers.get("Authorization"));
			useNodeAuthStore.getState().actions.setToken({ accessToken: "rotated-token", expiresAtUtc: "2026-10-15T12:00:00Z" });
			return config;
		});

		try {
			useNodeAuthStore.getState().actions.setToken({ accessToken: "old-token", expiresAtUtc: "2026-10-15T12:00:00Z" });
			await getNodeSettings().catch(() => undefined);
		} finally {
			client.instance.interceptors.request.eject(rotate);
		}

		expect(sentBeforeRotation).toEqual(["Bearer old-token"]);
		expect(sent).toEqual(["Bearer rotated-token"]);
	});
});
