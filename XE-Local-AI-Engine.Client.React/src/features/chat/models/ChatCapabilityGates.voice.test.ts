import { describe, expect, it } from "vitest";

import type { ChatCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";

function makeCapabilities(overrides: Partial<ChatCapabilities> = {}): ChatCapabilities {
	return {
		localRuntime: true,
		localModelManagement: true,
		localTools: true,
		toolApprovals: false,
		conversationFeedback: true,
		offlineFirst: false,
		encryptedConversations: false,
		clientNodeRouting: false,
		fileAttachments: true,
		imageAttachments: false,
		agentManagement: true,
		voice: true,
		...overrides,
	};
}

describe("buildChatUiCapabilities — voice gating", () => {
	it("hides voice controls when the manifest is disabled, even if the node surface flag is on", () => {
		const ui = buildChatUiCapabilities(makeCapabilities({ voice: true }), false);
		expect(ui.showVoiceControls).toBe(false);
	});

	it("shows voice controls only when both the surface flag and manifest.Enabled are true", () => {
		const ui = buildChatUiCapabilities(makeCapabilities({ voice: true }), true);
		expect(ui.showVoiceControls).toBe(true);
	});

	it("hides voice controls when the node surface flag is off, regardless of the manifest", () => {
		const ui = buildChatUiCapabilities(makeCapabilities({ voice: false }), true);
		expect(ui.showVoiceControls).toBe(false);
	});

	it("defaults manifest gate to false (module-level callers hide voice until a manifest-aware caller opts in)", () => {
		const ui = buildChatUiCapabilities(makeCapabilities({ voice: true }));
		expect(ui.showVoiceControls).toBe(false);
	});
});
