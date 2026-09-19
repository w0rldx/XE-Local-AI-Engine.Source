import { describe, expect, it } from "vitest";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";

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

	it("explains the hidden capability surfaces for the chat notice", () => {
		// Every surface the notice can name ships on in the default node profile, so nothing is listed.
		expect(hiddenChatSurfaceLabels(buildChatUiCapabilities(nodeCapabilities.chat))).toEqual([]);

		// It still names a surface that is genuinely off — otherwise the assertion above would pass on an empty list.
		const withApprovalsOff = buildChatUiCapabilities({ ...nodeCapabilities.chat, toolApprovals: false });
		expect(hiddenChatSurfaceLabels(withApprovalsOff)).toEqual(["tool approval controls"]);
	});
});
