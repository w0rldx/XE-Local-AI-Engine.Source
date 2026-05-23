import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import type { CreateClientConfig } from "@/core/api/generated/client.gen";
import { environment } from "@/Environment";

// FastEndpoints OpenAPI paths already include `/api/local/v1`, so generated SDK
// calls use the host root. Hand-written helpers can still use versioned URL utilities.
export const createClientConfig: CreateClientConfig = (config) => ({
	...config,
	axios: axiosInstance,
	baseURL: environment.VITE_API_URL.replace(/\/$/, ""),
	throwOnError: true,
});
