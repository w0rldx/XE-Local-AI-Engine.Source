import { describe, expect, it } from "vitest";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";

describe("chat capability gates", () => {
	it("hides node-irrelevant chat surfaces", () => {
		const capabilities = buildChatUiCapabilities(nodeCapabilities.chat);

		expect(capabilities).toMatchObject({
			showEncryptedConversationControls: false,
			showClientNodeRoutingControls: false,
			showToolApprovalControls: false,
			showConversationFeedbackControls: true,
			showFileAttachmentControls: true,
			showImageAttachmentControls: false,
			showLocalToolControls: true,
		});
	});

	it("explains the hidden capability surfaces for the chat notice", () => {
		const hiddenSurfaces = hiddenChatSurfaceLabels(buildChatUiCapabilities(nodeCapabilities.chat));

		expect(hiddenSurfaces).toEqual(["encrypted chat controls", "client-node routing controls", "tool approval controls"]);
	});
});
