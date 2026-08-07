import { describe, expect, it } from "vitest";

import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { getSlashCommandQuery, matchSlashCommands, resolveSlashCommand } from "@/features/chat/models/SlashCommandResolver";

const options: ChatCommandOption[] = [
	{ id: "3", name: "review-code", description: "Inspect the patch", prompt: "review code" },
	{ id: "2", name: "preview", description: "Show a draft", prompt: "preview" },
	{ id: "1", name: "review", description: "Check work", prompt: "review" },
	{ id: "4", name: "audit", description: "Review dependencies", prompt: "audit" },
];

describe("slash command matching", () => {
	it("ranks exact, prefix, name substring, and description substring before ordinal name", () => {
		expect(matchSlashCommands(options, "review").map((option) => option.name)).toEqual(["review", "review-code", "preview", "audit"]);
	});

	it("sorts an empty slash query by ordinal command name", () => {
		expect(matchSlashCommands(options, "").map((option) => option.name)).toEqual(["audit", "preview", "review", "review-code"]);
	});

	it("only activates for an initial slash token with a collapsed caret at the end", () => {
		expect(getSlashCommandQuery({ content: "/rev", selectionStart: 4, selectionEnd: 4, interactive: true, isComposing: false })).toBe("rev");
		expect(getSlashCommandQuery({ content: "say /rev", selectionStart: 8, selectionEnd: 8, interactive: true, isComposing: false })).toBeNull();
		expect(getSlashCommandQuery({ content: "/rev", selectionStart: 2, selectionEnd: 2, interactive: true, isComposing: false })).toBeNull();
		expect(getSlashCommandQuery({ content: "/rev", selectionStart: 1, selectionEnd: 3, interactive: true, isComposing: false })).toBeNull();
		expect(getSlashCommandQuery({ content: "/rev", selectionStart: 4, selectionEnd: 4, interactive: false, isComposing: false })).toBeNull();
		expect(getSlashCommandQuery({ content: "/rev", selectionStart: 4, selectionEnd: 4, interactive: true, isComposing: true })).toBeNull();
	});

	it("resolves only an exact known canonical slash command", () => {
		expect(resolveSlashCommand("/review", options)?.prompt).toBe("review");
		expect(resolveSlashCommand("/review extra", options)).toBeNull();
		expect(resolveSlashCommand(" /review ", options)).toBeNull();
		expect(resolveSlashCommand("/missing", options)).toBeNull();
	});
});
