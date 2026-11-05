import type { TFunction } from "i18next";

import type { AgentOption } from "@/core/agents/models/AgentOption";
import type { ModelOption } from "@/core/models/ModelOption";
import { toNodeChatRequestModel } from "@/core/models/NodeChatModelSelection";
import { isNodeChatTurnParkedConflict, isNodeChatWorkflowRunLiveConflict } from "@/features/chat/api/NodeChatConflict";
import {
	clientWatchdogFailureCategory,
	StreamWatchdogError,
	streamWatchdogNotice,
} from "@/features/chat/api/NodeChatStreamGuard";
import { chatWriteErrorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

/**
 * Only a concrete model id that is still an available option is sent. A stale persisted selection would route to a model
 * that isn't installed and fail with "model is not installed"; undefined makes the node resolve its configured default
 * (the provisioned GGUF). This guards the fast first-send case before the reconciliation effect has reset the store.
 */
export function resolveRequestModel(
	model: string,
	modelOptions: readonly ModelOption[],
	cloudModelOptions: readonly ModelOption[],
): string | undefined {
	const requestedConcreteModel = toNodeChatRequestModel(model);
	return requestedConcreteModel !== undefined &&
		(modelOptions.some((option) => option.value === requestedConcreteModel) ||
			cloudModelOptions.some((option) => option.value === requestedConcreteModel))
		? requestedConcreteModel
		: undefined;
}

/**
 * The agent a request resolves: agent mode is on, one is selected, AND it still exists in the live list (stale or
 * deleted ids fall back to the Default Assistant, which is no agent on the wire).
 */
export function effectiveAgent(
	agentModeEnabled: boolean,
	selectedAgentId: string,
	agentOptions: readonly AgentOption[],
): AgentOption | undefined {
	return agentModeEnabled && selectedAgentId ? agentOptions.find((agent) => agent.id === selectedAgentId) : undefined;
}

/** True for a placeholder-titled local conversation with no user message yet: its first send promotes the title. */
export function needsFirstSendTitle(conversation: ChatConversationModel): boolean {
	const hasPriorUserMessage = conversation.messages.some((message) => message.role === "user");
	const title = conversation.title.trim();
	return !hasPriorUserMessage && conversation.origin !== "remote" && (title.length === 0 || title === "New conversation");
}

/**
 * The failure a send surfaces. A send against a remote-origin conversation is rejected by the server-side mutation
 * guard; over SignalR that arrives as a HubException leading with the ReadOnlyConversation token, not a 409. The client
 * watchdog is the ONE failure the browser itself raises, so it says so: its raw message ("Local chat stream timed out
 * (inter-chunk-stall).") reads like a node timeout and makes a premature client-side give-up indistinguishable from the
 * node's own ceiling. It gets a translated sentence plus its own reason code; every other error keeps the backend's
 * text, which names which node-side bound fired.
 */
export function sendFailureMessage(error: unknown, t: TFunction): { message: string; failureCategory: string | undefined } {
	const watchdogNotice = error instanceof StreamWatchdogError ? streamWatchdogNotice(error.category) : undefined;
	let message: string;
	if (isNodeChatWorkflowRunLiveConflict(error)) {
		message = t("pages.chat.workflowRunLive", "This conversation is running a workflow; answer through it or stop it first.");
	} else if (isNodeChatTurnParkedConflict(error)) {
		message = t(
			"pages.chat.turnParked",
			"A reply in this conversation is waiting for your answer. Answer the waiting card, then send again.",
		);
	} else if (watchdogNotice) {
		message = t(watchdogNotice.key, watchdogNotice.fallback);
	} else {
		message = chatWriteErrorMessage(error, t);
	}
	return { message, failureCategory: watchdogNotice ? clientWatchdogFailureCategory : undefined };
}
