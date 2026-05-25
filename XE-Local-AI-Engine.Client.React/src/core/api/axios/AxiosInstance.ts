import axios from "axios";

import {
	addApiProblemDetailsInterceptor,
	addAuthRequestInterceptor,
	addRateLimitingInterceptor,
	addUnauthorizedErrorInterceptor,
} from "@/core/api/axios/Interceptors";

const axiosInstance = axios.create({
	baseURL: "/",
	withCredentials: true,
	headers: {
		"Content-Type": "application/json",
		Accept: "application/json",
	},
});

// Request interceptors
addAuthRequestInterceptor(axiosInstance);

// Response interceptors
addUnauthorizedErrorInterceptor(axiosInstance);
addRateLimitingInterceptor(axiosInstance);
addApiProblemDetailsInterceptor(axiosInstance);

export { axiosInstance };
