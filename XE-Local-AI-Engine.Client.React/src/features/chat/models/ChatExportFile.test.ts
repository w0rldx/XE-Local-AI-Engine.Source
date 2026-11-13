import i18next from "i18next";
import { describe, expect, it } from "vitest";

import { chatExportFileName, chatExportMarkdown, parseChatExportFile } from "@/features/chat/models/ChatExportFile";

describe("parseChatExportFile", () => {
	it.each([
		["not json", "notJson"],
		[JSON.stringify({ format: "xe-graph-workflow", formatVersion: 1, conversations: [] }), "wrongFormat"],
		[JSON.stringify({ format: "xe-chat-export", formatVersion: 1 }), "wrongFormat"],
		[JSON.stringify({ format: "xe-chat-export", formatVersion: 2, conversations: [] }), "unsupportedVersion"],
	])("refuses %s with %s", (text, reason) => {
		expect(parseChatExportFile(text)).toEqual({ ok: false, reason });
	});

	it("accepts a chat export as it is", () => {
		const document = { format: "xe-chat-export", formatVersion: 1, conversations: [] };

		expect(parseChatExportFile(JSON.stringify(document))).toEqual({ ok: true, document });
	});
});

describe("chatExportMarkdown", () => {
	it("renders the turns in order and names what the export leaves out", () => {
		const markdown = chatExportMarkdown(
			{
				title: "Trip plan",
				createdAtUtc: 1,
				attachments: [{ fileName: "map.pdf", sizeBytes: 3 }],
				messages: [
					{ role: "user", content: "Where to?", createdAtUtc: 2 },
					{ role: "assistant", content: "Lisbon.", createdAtUtc: 3, model: "qwen", toolCallCount: 2 },
				],
			},
			i18next.t,
		);

		expect(markdown).toBe(
			[
				"# Trip plan",
				"",
				"> Attached files, not included: map.pdf",
				"",
				"## User",
				"",
				"Where to?",
				"",
				"## Assistant (qwen)",
				"",
				"Lisbon.",
				"",
				"_2 tool calls, not included_",
				"",
			].join("\n"),
		);
	});
});

describe("chatExportFileName", () => {
	it.each([
		["Übersicht für Q3!", "md", "ubersicht-fur-q3.md"],
		["  ", "json", "chat.chat.json"],
		[null, "md", "chat.md"],
	] as const)("names %s as %s", (title, extension, expected) => {
		expect(chatExportFileName(title, extension)).toBe(expected);
	});
});
