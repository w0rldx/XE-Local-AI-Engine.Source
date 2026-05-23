import axios from "axios";

import { addApiProblemDetailsInterceptor, addRateLimitingInterceptor } from "@/core/api/axios/Interceptors";
import { versionedApiBaseUrl } from "@/core/api/utils/VersionedApiUrl";

const axiosInstance = axios.create({
	baseURL: versionedApiBaseUrl,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

// Response interceptors
addRateLimitingInterceptor(axiosInstance);
addApiProblemDetailsInterceptor(axiosInstance);

export { axiosInstance };
