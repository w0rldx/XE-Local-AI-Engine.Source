import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	type CreateGoldenConversationRequestDto,
	type GoldenConversation,
	type GoldenHarvestResult,
	toCreatedGoldenConversation,
	toGoldenConversations,
	toGoldenHarvestResult,
} from "@/features/agents/models/GoldenConversationModels";

// Playbook P4 + harvest follow-up — per-agent golden conversation CRUD (Operator-gated, mirror PlaybookActionsApi):
//   GET    /agents/{agentDefinitionId}/golden-conversations                       → list the agent's golden cases ({ items })
//   POST   /agents/{agentDefinitionId}/golden-conversations                       → create a golden case
//   POST   /agents/{agentDefinitionId}/golden-conversations/harvest               → harvest candidates from thumbs-up turns
//   POST   /agents/{agentDefinitionId}/golden-conversations/{goldenId}/approve    → approve a harvested candidate
//   DELETE /agents/{agentDefinitionId}/golden-conversations/{goldenId}            → delete / reject (ownership-guarded)
// Thin contract layer so the panel works against the documented endpoint; if the backend casing/route base
// differs, only this file changes. The read wires the TanStack Query AbortSignal in and validates the payload at
// the boundary via the Zod model. The harvest/approve POSTs are route-only — they send an empty `{}` body because
// FastEndpoints 415s a POST with no body.

const AGENTS_ROUTE = "agents";
const GOLDEN_SEGMENT = "golden-conversations";

function goldenConversationsRoute(agentDefinitionId: string): string {
	return `${AGENTS_ROUTE}/${encodeURIComponent(agentDefinitionId)}/${GOLDEN_SEGMENT}`;
}

function goldenConversationRoute(agentDefinitionId: string, goldenId: string): string {
	return `${goldenConversationsRoute(agentDefinitionId)}/${encodeURIComponent(goldenId)}`;
}

function harvestGoldenConversationsRoute(agentDefinitionId: string): string {
	return `${goldenConversationsRoute(agentDefinitionId)}/harvest`;
}

function approveGoldenConversationRoute(agentDefinitionId: string, goldenId: string): string {
	return `${goldenConversationRoute(agentDefinitionId, goldenId)}/approve`;
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

// Harvest golden candidates from the agent's thumbs-up assistant turns. Route-only POST: send an empty `{}` body so
// FastEndpoints does not 415 the request. Returns the scan/created/duplicate/skipped counts.
export async function harvestGolden(
	agentDefinitionId: string,
	config?: AxiosRequestConfig,
): Promise<GoldenHarvestResult> {
	const { data } = await axiosInstance.post<unknown>(
		buildLocalApiUrl(harvestGoldenConversationsRoute(agentDefinitionId)),
		{},
		config,
	);
	return toGoldenHarvestResult(data);
}

// Approve a harvested-but-disabled candidate, flipping it into the active golden set. Route-only POST (empty `{}`
// body). Returns the bare updated golden case, which shares the create response shape.
export async function approveGolden(
	agentDefinitionId: string,
	goldenId: string,
	config?: AxiosRequestConfig,
): Promise<GoldenConversation> {
	const { data } = await axiosInstance.post<unknown>(
		buildLocalApiUrl(approveGoldenConversationRoute(agentDefinitionId, goldenId)),
		{},
		config,
	);
	return toCreatedGoldenConversation(data);
}
