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
};

export function buildChatUiCapabilities(capabilities: ChatCapabilities): ChatUiCapabilities {
	return {
		showLocalToolControls: capabilities.localTools,
		showToolApprovalControls: capabilities.toolApprovals,
		showConversationFeedbackControls: capabilities.conversationFeedback,
		showEncryptedConversationControls: capabilities.encryptedConversations,
		showClientNodeRoutingControls: capabilities.clientNodeRouting,
		showFileAttachmentControls: capabilities.fileAttachments,
		showImageAttachmentControls: capabilities.imageAttachments,
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
