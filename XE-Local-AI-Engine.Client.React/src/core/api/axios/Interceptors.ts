import { AxiosHeaders, type AxiosError, type AxiosInstance } from "axios";
import { t } from "i18next";
import { toast } from "@/core/ui/notifications/Toast";

import { getLocalOperatorToken, localOperatorHeaderName } from "@/core/api/auth/LocalOperatorToken";
import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

export const addLocalOperatorTokenInterceptor = (axiosInstance: AxiosInstance) => {
	axiosInstance.interceptors.request.use((config) => {
		const token = getLocalOperatorToken();
		if (!token) {
			return config;
		}

		const headers = AxiosHeaders.from(config.headers);
		headers.set(localOperatorHeaderName, token);
		config.headers = headers;

		return config;
	});
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
