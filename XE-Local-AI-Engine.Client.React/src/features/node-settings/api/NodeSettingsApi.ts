import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export interface NodeSettingsDto {
	maxMessageRequestTimeoutSeconds: number;
	minMessageRequestTimeoutSeconds: number;
	maxAllowedMessageRequestTimeoutSeconds: number;
}

export interface SaveNodeSettingsRequestDto {
	maxMessageRequestTimeoutSeconds: number;
}

export async function getNodeSettings(config?: AxiosRequestConfig): Promise<NodeSettingsDto> {
	const { data } = await axiosInstance.get<NodeSettingsDto>(buildLocalApiUrl("node-settings"), config);
	return data;
}

export async function saveNodeSettings(request: SaveNodeSettingsRequestDto, config?: AxiosRequestConfig): Promise<NodeSettingsDto> {
	const { data } = await axiosInstance.put<NodeSettingsDto>(buildLocalApiUrl("node-settings"), request, config);
	return data;
}
