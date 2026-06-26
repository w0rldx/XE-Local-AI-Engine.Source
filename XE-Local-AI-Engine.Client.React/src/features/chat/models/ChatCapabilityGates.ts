import type { ChatCapabilities } from "@/capabilities/NodeCapabilities";
import type { ChatUiCapabilities } from "@/features/chat/models/ChatModels";

const hiddenSurfaceLabels: ReadonlyArray<readonly [keyof ChatUiCapabilities, string]> = [
	["showEncryptedConversationControls", "encrypted chat controls"],
	["showClientNodeRoutingControls", "client-node routing controls"],
	["showToolApprovalControls", "tool approval controls"],
	["showConversationFeedbackControls", "conversation feedback controls"],
];

export const defaultChatUiCapabilities: ChatUiCapabilities = {
	showLocalToolControls: false,
	showToolApprovalControls: false,
	showConversationFeedbackControls: false,
	showEncryptedConversationControls: false,
	showClientNodeRoutingControls: false,
	showFileAttachmentControls: false,
	showImageAttachmentControls: false,
	showAgentControls: false,
	showVoiceControls: false,
};

// `manifestVoiceEnabled` is the operator-owned manifest.Enabled gate (server-state, plan §7.1): voice UI is shown
// only when the node ships the voice surface AND the operator has enabled it on this node. Defaults to false so the
// module-level call sites (which lack the runtime manifest) keep voice hidden until a manifest-aware caller opts in.
export function buildChatUiCapabilities(
	capabilities: ChatCapabilities,
	manifestVoiceEnabled = false,
): ChatUiCapabilities {
	return {
		showLocalToolControls: capabilities.localTools,
		showToolApprovalControls: capabilities.toolApprovals,
		showConversationFeedbackControls: capabilities.conversationFeedback,
		showEncryptedConversationControls: capabilities.encryptedConversations,
		showClientNodeRoutingControls: capabilities.clientNodeRouting,
		showFileAttachmentControls: capabilities.fileAttachments,
		showImageAttachmentControls: capabilities.imageAttachments,
		// Agent controls are shown when agent management is available (node-local CRUD backed). The
		// capability derives from nodeCapabilities.agentManagement at the call site in Chat.tsx.
		showAgentControls: capabilities.agentManagement ?? false,
		// Voice controls require BOTH the node `voice` surface flag AND the operator-owned manifest.Enabled.
		showVoiceControls: (capabilities.voice ?? false) && manifestVoiceEnabled,
	};
}

export function hiddenChatSurfaceLabels(capabilities: ChatUiCapabilities): string[] {
	const labels: string[] = [];
	for (const [key, label] of hiddenSurfaceLabels) {
		if (!capabilities[key]) {
			labels.push(label);
		}
	}

	return labels;
}
