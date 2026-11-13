// What the node config pickers offer: tools, agent definitions and the three model lists.

import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated";
import {
	listAgentDefinitionsOptions,
	listGraphWorkflowToolsOptions,
	listLocalModelsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { EXTERNAL_PROVIDER, LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import type { FeedOptions } from "@/features/graphWorkflows/queries/useGraphWorkflows";

/**
 * The tools a Tool node may name. Already filtered server-side to the runnable envelope (`ReadLocal` and no composed
 * approval), so the picker offers exactly the runnable set and must NEVER re-derive eligibility here.
 */
export function useGraphWorkflowTools(options: FeedOptions = {}) {
	return useQuery({
		...listGraphWorkflowToolsOptions(),
		enabled: options.enabled ?? true,
	});
}

/**
 * Agent definitions, for an Agent node's optional `agentDefinitionId`. Read straight off the generated client rather
 * than through `features/agents`' own hook: this needs the ids and names and nothing else, and reaching into another
 * feature for a two-field projection is architecture debt the dependency baseline would have to carry forever.
 */
export function useGraphWorkflowAgentOptions(options: FeedOptions = {}) {
	return useQuery({
		...listAgentDefinitionsOptions(),
		enabled: options.enabled ?? true,
		select: (data) =>
			(data.items ?? []).map((agent) => ({
				value: agent.id,
				label: agent.name,
				requiresTools: agent.allowedToolNames.length > 0 || agent.kind === "Orchestrator",
			})),
	});
}

/** The agent definitions fetched on demand, for a sample's create to bind its `repository` nodes (`bindRepositoryAgent`). */
export function useFetchGraphWorkflowAgents() {
	const queryClient = useQueryClient();
	return useCallback(async () => (await queryClient.fetchQuery(listAgentDefinitionsOptions())).items ?? [], [queryClient]);
}

// The catalog's cloud provider tags (backend `LocalModelProviders`). Duplicated from the chat feature's picker on
// purpose: a feature never imports another feature, and two string constants do not earn a core module.
const CODEX_PROVIDER = "CodexOAuth";
const AZURE_FOUNDRY_PROVIDER = "AzureFoundry";

/**
 * Where a catalog entry leaves the node, for the picker label; undefined for a node-local model. An external entry
 * is cloud unless it positively declares `local`, the same fail-closed reading the backend's trust resolver takes.
 */
function cloudProviderOf(model: LocalModelResponse): string | undefined {
	if (model.provider === CODEX_PROVIDER) {
		return "Codex";
	}
	if (model.provider === AZURE_FOUNDRY_PROVIDER) {
		return "Azure AI Foundry";
	}
	if (model.provider === EXTERNAL_PROVIDER && model.declaredLocality !== "local") {
		return model.externalConnectionName ?? model.externalConnectionId ?? EXTERNAL_PROVIDER;
	}
	return undefined;
}

function toGraphWorkflowModelOption(model: LocalModelResponse) {
	const cloudProvider = cloudProviderOf(model);
	return {
		value: model.modelName ?? "",
		label: model.displayLabel ?? model.modelName ?? "",
		isToolCapable: model.isToolCapable,
		...(cloudProvider === undefined ? {} : { cloudProvider }),
	};
}

/**
 * Chat models on this node, for an Agent node's `model` override. Same filter the chat picker applies (`kind` is
 * `Chat`), because a graph node that names a non-chat model is a run that fails at dispatch. Cloud and external
 * entries are listed too; whether a cloud one may run unattended is the server's call at run start.
 */
export function useGraphWorkflowModelOptions(options: FeedOptions = {}) {
	return useQuery({
		...listLocalModelsOptions(),
		enabled: options.enabled ?? true,
		select: (data) => (data.items ?? []).filter((model) => model.kind === "Chat").map(toGraphWorkflowModelOption),
	});
}

/**
 * An LLM Call (and the DecisionModel lane lowered onto it) dispatches through an installed node-managed GGUF chat
 * model, or through a cloud one. An external endpoint declared `local` is neither, so the run would refuse it.
 */
export function useGraphWorkflowLlmModelOptions(options: FeedOptions = {}) {
	return useQuery({
		...listLocalModelsOptions(),
		enabled: options.enabled ?? true,
		select: (data) =>
			(data.items ?? [])
				.filter(
					(model) => model.kind === "Chat" && (model.provider === LLAMACPP_PROVIDER || cloudProviderOf(model) !== undefined),
				)
				.map(toGraphWorkflowModelOption),
	});
}

/**
 * A `systemone` DecisionModel node runs a native decision model through llama-server, so only installed
 * node-managed GGUFs classified `Decision` qualify. Same list query as above, so no extra request.
 */
export function useGraphWorkflowDecisionModelOptions(options: FeedOptions = {}) {
	return useQuery({
		...listLocalModelsOptions(),
		enabled: options.enabled ?? true,
		select: (data) =>
			(data.items ?? [])
				.filter((model) => model.kind === "Decision" && model.provider === LLAMACPP_PROVIDER)
				.map(toGraphWorkflowModelOption),
	});
}
