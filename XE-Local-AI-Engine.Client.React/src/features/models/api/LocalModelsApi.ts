import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export interface LocalModelDto {
  modelName: string;
  sizeBytes: number | null;
  modifiedAtUtc: number | null;
  family: string | null;
  parameterSize: string | null;
  quantizationLevel: string | null;
  isSelected: boolean;
}

export interface ListLocalModelsResponseDto {
  isAvailable: boolean;
  selectedModelName: string | null;
  configuredDefaultModelName: string | null;
  error: string | null;
  items: LocalModelDto[];
}

export interface LocalModelDetailsDto {
  modelName: string;
  maxContextTokens: number | null;
  template: string | null;
  system: string | null;
  license: string | null;
}

export interface SelectLocalModelRequestDto {
  modelName: string;
}

export interface SelectLocalModelResponseDto {
  selectedModelName: string;
}

export interface PullLocalModelRequestDto {
  modelName: string;
}

export interface PullLocalModelResponseDto {
  modelName: string;
  status: string;
  totalBytes: number | null;
  completedBytes: number | null;
}

export interface DeleteLocalModelResponseDto {
  modelName: string;
  deleted: boolean;
}

function encodeModelRouteSegment(modelName: string): string {
  return encodeURIComponent(modelName.trim());
}

export async function listLocalModels(config?: AxiosRequestConfig): Promise<ListLocalModelsResponseDto> {
  const { data } = await axiosInstance.get<ListLocalModelsResponseDto>(buildLocalApiUrl("models"), config);
  return data;
}

export async function getLocalModelDetails(modelName: string, config?: AxiosRequestConfig): Promise<LocalModelDetailsDto> {
  const { data } = await axiosInstance.get<LocalModelDetailsDto>(buildLocalApiUrl(`models/${encodeModelRouteSegment(modelName)}/details`), config);
  return data;
}

export async function selectLocalModel(request: SelectLocalModelRequestDto, config?: AxiosRequestConfig): Promise<SelectLocalModelResponseDto> {
  const { data } = await axiosInstance.post<SelectLocalModelResponseDto>(buildLocalApiUrl("models/select"), request, config);
  return data;
}

export async function pullLocalModel(request: PullLocalModelRequestDto, config?: AxiosRequestConfig): Promise<PullLocalModelResponseDto> {
  const { data } = await axiosInstance.post<PullLocalModelResponseDto>(buildLocalApiUrl("models/pull"), request, config);
  return data;
}

export async function deleteLocalModel(modelName: string, config?: AxiosRequestConfig): Promise<DeleteLocalModelResponseDto> {
  const { data } = await axiosInstance.delete<DeleteLocalModelResponseDto>(buildLocalApiUrl(`models/${encodeModelRouteSegment(modelName)}`), config);
  return data;
}
