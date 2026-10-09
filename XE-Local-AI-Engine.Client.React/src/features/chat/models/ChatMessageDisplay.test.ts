import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import { deriveChatMessageDisplay } from "@/features/chat/models/ChatMessageDisplay";
import type { ChatMessageModel } from "@/features/chat/models/ChatModels";
import de from "@/locales/de.json";

function labelFor(role: ChatMessageModel["role"]): string {
	const message: ChatMessageModel = {
		id: "m1",
		conversationId: "c1",
		role,
		content: "hello",
		status: "completed",
		createdAt: "2026-09-24T10:00:00Z",
		sortOrder: 0,
	};
	return deriveChatMessageDisplay({
		message,
		isStreaming: false,
		showFeedbackControls: false,
		isWorkSessionConversation: false,
		t: i18next.t,
	}).label;
}

describe("deriveChatMessageDisplay role label", () => {
	afterEach(async () => {
		// The app's real i18next instance is shared by this file's tests.
		await i18next.changeLanguage("en");
	});

	it("labels the user and assistant turns from the bundle (F-37)", async () => {
		expect(labelFor("user")).toBe("You");
		expect(labelFor("assistant")).toBe("Assistant");

		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		expect(labelFor("user")).toBe(de.pages.chat.roles.you);
		expect(labelFor("user")).toBe("Du");
		expect(labelFor("assistant")).toBe("Assistent");
	});
});

describe("deriveChatMessageDisplay agent name", () => {
	afterEach(async () => {
		await i18next.changeLanguage("en");
	});

	function agentNameFor(agentName: string | undefined): string | undefined {
		return deriveChatMessageDisplay({
			message: {
				id: "m1",
				conversationId: "c1",
				role: "assistant",
				content: "hello",
				status: "completed",
				createdAt: "2026-09-24T10:00:00Z",
				sortOrder: 0,
				...(agentName === undefined ? {} : { agentName }),
			},
			isStreaming: false,
			showFeedbackControls: false,
			isWorkSessionConversation: false,
			t: i18next.t,
		}).agentDisplayName;
	}

	// The node stamps the seeded persona's English data-row name on every default turn; a German reader saw it as is.
	it("shows the seeded default persona in the reader's language and any other agent verbatim", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");

		expect(agentNameFor("Default Assistant")).toBe(de.pages.chat.defaultAgentName);
		expect(agentNameFor(undefined)).toBe(de.pages.chat.defaultAgentName);
		expect(agentNameFor("Mathematician")).toBe("Mathematician");
	});
});
