import axios from "axios";

import {
	addApiProblemDetailsInterceptor,
	addBearerReconciliationInterceptor,
	addFormDataContentTypeInterceptor,
	addRateLimitingInterceptor,
	addUnauthorizedErrorInterceptor,
	addVaultLockedInterceptor,
} from "@/core/api/axios/Interceptors";
import { addDiagnosticsNetworkInterceptor } from "@/core/diagnostics/collectors/Network.axios";

const axiosInstance = axios.create({
	// Same-origin relative base. MUST stay "" (not "/"). The generated hey-api client
	// (Generated.runtime.ts) passes an empty per-request baseURL; hey-api's buildUrl treats
	// a falsy baseURL as "unset" and falls back to THIS instance default. With "/" here,
	// getUrl emits "/" + "/api/local/v1/<path>" = "//api/local/v1/<path>" — a protocol-relative
	// URL the browser parses with host "api" (ERR_NAME_NOT_RESOLVED, request hangs forever).
	// "" keeps the generated SDK calls same-origin.
	baseURL: "",
	withCredentials: true,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

// The bearer comes from the generated client's `auth` callback (Generated.runtime.ts); this only refreshes its value.
addFormDataContentTypeInterceptor(axiosInstance);
addBearerReconciliationInterceptor(axiosInstance);

addUnauthorizedErrorInterceptor(axiosInstance);
addVaultLockedInterceptor(axiosInstance);
addRateLimitingInterceptor(axiosInstance);
addApiProblemDetailsInterceptor(axiosInstance);

// Diagnostics trace/network collector — registered LAST so its request interceptor injects the
// `traceparent` after auth and its response interceptor records the final outcome.
addDiagnosticsNetworkInterceptor(axiosInstance);

export { axiosInstance };
