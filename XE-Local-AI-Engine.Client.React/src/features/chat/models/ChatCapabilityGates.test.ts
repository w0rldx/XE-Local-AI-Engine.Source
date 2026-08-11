import { describe, expect, it } from "vitest";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";

describe("chat capability gates", () => {
	it("hides node-irrelevant chat surfaces", () => {
		const capabilities = buildChatUiCapabilities(nodeCapabilities.chat);

		expect(capabilities).toMatchObject({
			showEncryptedConversationControls: false,
			showClientNodeRoutingControls: false,
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
		const hiddenSurfaces = hiddenChatSurfaceLabels(buildChatUiCapabilities(nodeCapabilities.chat));

		// Tool-approval controls are no longer hidden; only the encrypted/client-node surfaces stay off.
		expect(hiddenSurfaces).toEqual(["encrypted chat controls", "client-node routing controls"]);
	});
});
