import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export interface CloudSettingsDto {
	providerName: string;
	endpoint?: string | null;
	deploymentName?: string | null;
	hasStoredApiKey: boolean;
}

export interface SaveCloudSettingsRequestDto {
	providerName: "AzureFoundry";
	endpoint: string;
	apiKey: string;
	deploymentName: string;
}

export async function getCloudSettings(config?: AxiosRequestConfig): Promise<CloudSettingsDto> {
	const { data } = await axiosInstance.get<CloudSettingsDto>(buildLocalApiUrl("cloud-settings"), config);
	return data;
}

export async function saveCloudSettings(request: SaveCloudSettingsRequestDto, config?: AxiosRequestConfig): Promise<CloudSettingsDto> {
	const { data } = await axiosInstance.put<CloudSettingsDto>(buildLocalApiUrl("cloud-settings"), request, config);
	return data;
}

export async function clearCloudSettings(config?: AxiosRequestConfig): Promise<CloudSettingsDto> {
	const { data } = await axiosInstance.delete<CloudSettingsDto>(buildLocalApiUrl("cloud-settings"), config);
	return data;
}
