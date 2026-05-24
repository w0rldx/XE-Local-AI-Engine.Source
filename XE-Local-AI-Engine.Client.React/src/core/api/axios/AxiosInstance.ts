import axios from "axios";

import { addApiProblemDetailsInterceptor, addLocalOperatorTokenInterceptor, addRateLimitingInterceptor } from "@/core/api/axios/Interceptors";
import { versionedApiBaseUrl } from "@/core/api/utils/VersionedApiUrl";

const axiosInstance = axios.create({
	baseURL: versionedApiBaseUrl,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

// Request interceptors
addLocalOperatorTokenInterceptor(axiosInstance);

// Response interceptors
addRateLimitingInterceptor(axiosInstance);
addApiProblemDetailsInterceptor(axiosInstance);

export { axiosInstance };
