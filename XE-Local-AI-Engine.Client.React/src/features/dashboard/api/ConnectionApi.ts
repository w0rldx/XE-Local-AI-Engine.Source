import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export interface ConnectionStatusDto {
	state: string;
	lastError?: string | null;
	lastUpdatedAt: string;
	isPaired: boolean;
	autoConnectOnStart: boolean;
	bindingMethod?: string | null;
	lastKnownNodeName?: string | null;
	tokenExpiresAt?: string | null;
	canConnect: boolean;
	canDisconnect: boolean;
	canEnableAutoConnect: boolean;
	canDisableAutoConnect: boolean;
}

export async function getConnectionStatus(config?: AxiosRequestConfig): Promise<ConnectionStatusDto> {
	const { data } = await axiosInstance.get<ConnectionStatusDto>(buildLocalApiUrl("connection"), config);
	return data;
}

export async function connectWorker(config?: AxiosRequestConfig): Promise<ConnectionStatusDto> {
	const { data } = await axiosInstance.post<ConnectionStatusDto>(buildLocalApiUrl("connection/connect"), undefined, config);
	return data;
}

export async function disconnectWorker(config?: AxiosRequestConfig): Promise<ConnectionStatusDto> {
	const { data } = await axiosInstance.post<ConnectionStatusDto>(buildLocalApiUrl("connection/disconnect"), undefined, config);
	return data;
}

export async function enableAutoConnect(config?: AxiosRequestConfig): Promise<ConnectionStatusDto> {
	const { data } = await axiosInstance.post<ConnectionStatusDto>(buildLocalApiUrl("connection/auto-connect/enable"), undefined, config);
	return data;
}

export async function disableAutoConnect(config?: AxiosRequestConfig): Promise<ConnectionStatusDto> {
	const { data } = await axiosInstance.post<ConnectionStatusDto>(buildLocalApiUrl("connection/auto-connect/disable"), undefined, config);
	return data;
}
