import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export interface NodeBindingSessionDto {
	deviceCode: string;
	userCode: string;
	verificationUri: string;
	verificationUriComplete: string;
	expiresAt: string;
	intervalSeconds: number;
	status: string;
}

export interface PollNodeBindingSessionResponseDto {
	status: string;
	intervalSeconds: number;
	expiresAt?: string | null;
}

export interface CancelNodeBindingResponseDto {
	cancelled: boolean;
}

export async function startNodeBinding(config?: AxiosRequestConfig): Promise<NodeBindingSessionDto> {
	const { data } = await axiosInstance.post<NodeBindingSessionDto>(buildLocalApiUrl("binding/start"), undefined, config);
	return data;
}

export async function pollNodeBinding(session: NodeBindingSessionDto, config?: AxiosRequestConfig): Promise<PollNodeBindingSessionResponseDto> {
	const { data } = await axiosInstance.post<PollNodeBindingSessionResponseDto>(
		buildLocalApiUrl("binding/poll"),
		{
			deviceCode: session.deviceCode,
			userCode: session.userCode,
			verificationUri: session.verificationUri,
			verificationUriComplete: session.verificationUriComplete,
			expiresAt: session.expiresAt,
			intervalSeconds: session.intervalSeconds,
		},
		config,
	);
	return data;
}

export async function cancelNodeBinding(config?: AxiosRequestConfig): Promise<CancelNodeBindingResponseDto> {
	const { data } = await axiosInstance.post<CancelNodeBindingResponseDto>(buildLocalApiUrl("binding/cancel"), undefined, config);
	return data;
}
