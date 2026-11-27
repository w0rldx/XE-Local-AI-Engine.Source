import { describe, expect, it } from "vitest";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities } from "@/core/chat/models/ChatCapabilityGates";

describe("chat capability gates", () => {
	it("surfaces the node's local chat controls", () => {
		const capabilities = buildChatUiCapabilities(nodeCapabilities.chat);

		expect(capabilities).toMatchObject({
			// The local tool-approval responder ships, so the approval controls are surfaced by default.
			showToolApprovalControls: true,
			showConversationFeedbackControls: true,
			showFileAttachmentControls: true,
			// Image attachments route to vision-capable models via the mmproj path (still gated per-model on
			// activeModelMultimodal in ChatInputArea).
			showImageAttachmentControls: true,
			showLocalToolControls: true,
		});
	});
});
