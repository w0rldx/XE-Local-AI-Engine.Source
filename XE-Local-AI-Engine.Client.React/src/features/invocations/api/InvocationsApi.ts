import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

export type InvocationStatusDto = "Pending" | "Assigned" | "Running" | "Completed" | "Failed" | "Cancelled";

export type InvocationFailureCategoryDto = string | null;

export interface InvocationCurrentDto {
  invocationId: string;
  conversationId: string;
  status: InvocationStatusDto;
  modelUsed: string | null;
  startedAt: string;
  lastUpdatedAt: string;
  completedAt: string | null;
  error: string | null;
  failureCategory: InvocationFailureCategoryDto;
  streamedChunkCount: number;
  streamedThinkingChunkCount: number;
  pendingToolCallCount: number;
  hasPendingApproval: boolean;
}

export interface InvocationHistoryDto {
  invocationId: string;
  conversationId: string;
  status: InvocationStatusDto;
  modelUsed: string | null;
  startedAt: string;
  completedAt: string;
  durationMs: number;
  error: string | null;
  failureCategory: InvocationFailureCategoryDto;
  streamedChunkCount: number;
  streamedThinkingChunkCount: number;
}

export interface InvocationMonitorResponseDto {
  current: InvocationCurrentDto | null;
  history: InvocationHistoryDto[];
  historyCapacity: number;
}

export async function getInvocationMonitor(config?: AxiosRequestConfig): Promise<InvocationMonitorResponseDto> {
  const { data } = await axiosInstance.get<InvocationMonitorResponseDto>(buildLocalApiUrl("invocations"), config);
  return data;
}
