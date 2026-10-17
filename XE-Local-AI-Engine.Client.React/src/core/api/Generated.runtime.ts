import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import type { CreateClientConfig } from "@/core/api/generated/client.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// FastEndpoints OpenAPI paths already include `/api/local/v1`, so generated SDK
// calls use the host root. Hand-written helpers can still use versioned URL utilities.
// `baseURL` MUST stay "" (not "/"): see AxiosInstance.ts for the protocol-relative URL it would produce.
// `auth` feeds hey-api's per-operation `security`: only secured operations get the bearer, read at request time.
export const createClientConfig: CreateClientConfig = (config) => ({
	...config,
	axios: axiosInstance,
	baseURL: "",
	auth: () => useNodeAuthStore.getState().accessToken ?? undefined,
});
