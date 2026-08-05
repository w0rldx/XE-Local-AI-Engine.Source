import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from "axios";
import { t } from "i18next";
import { toast } from "@/core/ui/notifications/Toast";

import { ApiError } from "@/core/api/errors/ApiError";
import { NetworkError } from "@/core/api/errors/NetworkError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import { refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { router } from "@/core/integrations/tanstack-router/Router";

const retriedRequests = new WeakSet<InternalAxiosRequestConfig>();
let isRedirectingToLogin = false;

function redirectToLoginOnce(): void {
	if (globalThis.location.pathname === "/login" || globalThis.location.pathname === "/setup") {
		return;
	}

	if (isRedirectingToLogin) {
		return;
	}

	isRedirectingToLogin = true;

	Promise.resolve(
		router.navigate({
			to: "/login",
			search: {
				redirect: globalThis.location.pathname + globalThis.location.search,
			},
		}),
	)
		.finally(() => {
			isRedirectingToLogin = false;
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

			try {
				const token = await refreshNodeAuthToken();
				useNodeAuthStore.getState().actions.setToken(token);
				retriedRequests.add(requestConfig);
				requestConfig.headers.Authorization = `Bearer ${token.accessToken}`;
				return axiosInstance(requestConfig);
			} catch (refreshError) {
				useNodeAuthStore.getState().actions.clear();
				redirectToLoginOnce();
				return Promise.reject(refreshError);
			}
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
