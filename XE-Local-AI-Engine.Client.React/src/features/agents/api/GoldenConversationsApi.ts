import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	type CreateGoldenConversationRequestDto,
	type GoldenConversation,
	toCreatedGoldenConversation,
	toGoldenConversations,
} from "@/features/agents/models/GoldenConversationModels";

// Playbook P4 — per-agent golden conversation CRUD (Operator-gated, mirror PlaybookActionsApi):
//   GET    /agents/{agentDefinitionId}/golden-conversations              → list the agent's golden cases ({ items })
//   POST   /agents/{agentDefinitionId}/golden-conversations              → create a golden case
//   DELETE /agents/{agentDefinitionId}/golden-conversations/{goldenId}   → delete (ownership-guarded)
// Thin contract layer so the panel works against the documented endpoint; if the backend casing/route base
// differs, only this file changes. The read wires the TanStack Query AbortSignal in and validates the payload at
// the boundary via the Zod model.

const AGENTS_ROUTE = "agents";
const GOLDEN_SEGMENT = "golden-conversations";

function goldenConversationsRoute(agentDefinitionId: string): string {
	return `${AGENTS_ROUTE}/${encodeURIComponent(agentDefinitionId)}/${GOLDEN_SEGMENT}`;
}

function goldenConversationRoute(agentDefinitionId: string, goldenId: string): string {
	return `${goldenConversationsRoute(agentDefinitionId)}/${encodeURIComponent(goldenId)}`;
}

export async function listGoldenConversations(
	agentDefinitionId: string,
	config?: AxiosRequestConfig,
): Promise<GoldenConversation[]> {
	const { data } = await axiosInstance.get<unknown>(
		buildLocalApiUrl(goldenConversationsRoute(agentDefinitionId)),
		config,
	);
	return toGoldenConversations(data);
}

export async function createGoldenConversation(
	agentDefinitionId: string,
	request: CreateGoldenConversationRequestDto,
	config?: AxiosRequestConfig,
): Promise<GoldenConversation> {
	const { data } = await axiosInstance.post<unknown>(
		buildLocalApiUrl(goldenConversationsRoute(agentDefinitionId)),
		request,
		config,
	);
	return toCreatedGoldenConversation(data);
}

export async function deleteGoldenConversation(
	agentDefinitionId: string,
	goldenId: string,
	config?: AxiosRequestConfig,
): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(goldenConversationRoute(agentDefinitionId, goldenId)), config);
}
