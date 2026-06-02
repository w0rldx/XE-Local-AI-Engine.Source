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
  // Effective classification (override ?? detected) as a ModelKind string ("Chat" | "Embedding" | "Unknown").
  kind: string;
  // Machine-detected classification as a ModelKind string; drives the "reset to detected" affordance.
  detectedKind: string;
  // Raw Ollama capability strings for read-only badges (e.g. "tools", "vision", "thinking").
  capabilities: string[];
  // True when an operator override is set, so the effective kind differs from the detected one.
  isOverridden: boolean;
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

export interface ModelKindResponseDto {
  modelName: string;
  kind: string;
  detectedKind: string;
  capabilities: string[];
  isOverridden: boolean;
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

export async function setLocalModelKind(modelName: string, kind: string, config?: AxiosRequestConfig): Promise<ModelKindResponseDto> {
  const { data } = await axiosInstance.put<ModelKindResponseDto>(
    buildLocalApiUrl(`models/${encodeModelRouteSegment(modelName)}/kind`),
    { kind },
    config,
  );
  return data;
}

export async function resetLocalModelKind(modelName: string, config?: AxiosRequestConfig): Promise<ModelKindResponseDto> {
  const { data } = await axiosInstance.delete<ModelKindResponseDto>(
    buildLocalApiUrl(`models/${encodeModelRouteSegment(modelName)}/kind`),
    config,
  );
  return data;
}
