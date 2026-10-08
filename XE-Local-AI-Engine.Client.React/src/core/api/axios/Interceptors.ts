import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from "axios";
import { t } from "i18next";
import { toast } from "@/core/ui/notifications/Toast";

import { ApiError } from "@/core/api/errors/ApiError";
import { NetworkError } from "@/core/api/errors/NetworkError";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import { navigateToLogin, navigateToVault } from "@/core/api/axios/LoginNavigation";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { resetVaultSettled } from "@/core/auth/utils/SessionRestore";

const retriedRequests = new WeakSet<InternalAxiosRequestConfig>();
let isRedirectingToLogin = false;

function redirectToLoginOnce(): void {
	if (["/login", "/setup", "/vault", "/vault-setup"].includes(globalThis.location.pathname)) {
		return;
	}

	if (isRedirectingToLogin) {
		return;
	}

	isRedirectingToLogin = true;

	// Through the registered navigator, never a router import: see LoginNavigation.ts for the cycle that would close.
	Promise.resolve(navigateToLogin(globalThis.location.pathname + globalThis.location.search))
		.finally(() => {
			isRedirectingToLogin = false;
		})
		.catch(() => undefined);
}

let isRedirectingToVault = false;

function redirectToVaultOnce(): void {
	if (globalThis.location.pathname === "/vault" || isRedirectingToVault) {
		return;
	}

	isRedirectingToVault = true;
	Promise.resolve(navigateToVault())
		.finally(() => {
			isRedirectingToVault = false;
		})
		.catch(() => undefined);
}

export const addFormDataContentTypeInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.request.use((request) => {
		// The instance defaults Content-Type to application/json. For a FormData body that default is not just
		// wrong but actively harmful: axios' transformRequest re-serialises FormData to a JSON object whenever a
		// JSON content-type is present, silently downgrading every multipart request (skill import, chat/KB file
		// uploads) to a JSON body the multipart-only endpoints reject with 415. Drop the header for FormData so the
		// browser sets multipart/form-data with its boundary; plain JSON requests never enter this branch.
		if (typeof FormData !== "undefined" && request.data instanceof FormData) {
			request.headers.delete("Content-Type");
		}

		return request;
	});
};

export const addAuthRequestInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.request.use((request) => {
		const accessToken = useNodeAuthStore.getState().accessToken;
		if (accessToken) {
			request.headers.Authorization = `Bearer ${accessToken}`;
		}

		return request;
	});
};

export const addUnauthorizedErrorInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.response.use(
		(response) => response,
		async (error: AxiosError) => {
			if (error.response?.status !== 401) {
				return Promise.reject(error);
			}

			const requestConfig = error.config;
			if (!requestConfig || retriedRequests.has(requestConfig)) {
				useNodeAuthStore.getState().actions.clear();
				redirectToLoginOnce();
				return Promise.reject(error);
			}

			// A late 401 for a request sent with an older token: a refresh already rotated it, so replay with the current one.
			// Refreshing again per late 401 spends the auth rate limit that login, refresh and every tab share.
			const sentAuthorization = requestConfig.headers.Authorization;
			const currentToken = useNodeAuthStore.getState().accessToken;
			if (typeof sentAuthorization === "string" && currentToken && sentAuthorization !== `Bearer ${currentToken}`) {
				retriedRequests.add(requestConfig);
				requestConfig.headers.Authorization = `Bearer ${currentToken}`;
				return axiosInstance(requestConfig);
			}

			try {
				const token = await refreshNodeAuthToken();
				useNodeAuthStore.getState().actions.setToken(token);
				retriedRequests.add(requestConfig);
				requestConfig.headers.Authorization = `Bearer ${token.accessToken}`;
				return axiosInstance(requestConfig);
			} catch (refreshError) {
				// Only a 401 from refresh ends the session; a 429, 5xx or network failure leaves the cookie valid.
				if (getErrorStatus(refreshError) === 401) {
					useNodeAuthStore.getState().actions.clear();
					redirectToLoginOnce();
				}

				return Promise.reject(refreshError);
			}
		},
	);
};

// A locked engine answers every protected route with 503 "Vault locked". A tab that stayed open across an engine
// restart still holds a token and a settled vault cache, so nothing else would send it to the unlock page. Only this
// exact title counts: any other 503 is an ordinary outage and stays an error for the caller.
export const addVaultLockedInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.response.use(
		(response) => response,
		(error: AxiosError) => {
			const problem = error.response?.data as ProblemDetails | undefined;
			if (error.response?.status === 503 && problem?.title === "Vault locked") {
				resetVaultSettled();
				useNodeAuthStore.getState().actions.clear();
				redirectToVaultOnce();
			}

			return Promise.reject(error);
		},
	);
};

export const addRateLimitingInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.response.use(
		(response) => response,
		(error: AxiosError) => {
			if (error.response?.status === 429) {
				toast.error(t("errorMessages.tooManyRequests"));
			}
			return Promise.reject(error);
		},
	);
};

export const addApiProblemDetailsInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.response.use(
		(response) => response,
		(error: AxiosError) => {
			// A typed, message-less error rather than `new Error("Network error")`. That literal was untranslated and
			// unactionable, and because every helper renders `error.message` verbatim it became the ONLY thing every
			// page said when the node went away. NetworkError carries the case, not the copy; apiErrorMessage turns it
			// into a localized sentence at render time.
			if (error.code === "ERR_NETWORK") {
				throw new NetworkError();
			}

			if (
				error.response &&
				error.response.status !== 200 &&
				error.response.status !== 201 &&
				error.response.status !== 204 &&
				error.response.status !== 401 &&
				error.response.status !== 429
			) {
				const problemDetails = error.response?.data as ProblemDetails;

				throw new ApiError(error.response.status, problemDetails);
			}

			return Promise.reject(error);
		},
	);
};
