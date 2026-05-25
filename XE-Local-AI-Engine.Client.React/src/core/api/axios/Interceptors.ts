import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from "axios";
import { t } from "i18next";
import { toast } from "@/core/ui/notifications/Toast";

import { ApiError } from "@/core/api/errors/ApiError";
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
			if (error.code === "ERR_NETWORK") {
				throw new Error("Network error");
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
