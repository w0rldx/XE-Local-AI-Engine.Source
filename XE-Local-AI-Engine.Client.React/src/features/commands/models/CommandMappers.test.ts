import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse } from "@/core/api/generated";
import { toSlashCommand, toSlashCommands } from "@/features/commands/models/CommandMappers";

function response(
	overrides: Partial<XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse> = {},
): XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse {
	return {
		id: "command-id",
		name: "review",
		description: "Review work",
		source: "custom",
		action: { type: "sendPrompt", prompt: "Review the work" },
		...overrides,
	};
}

describe("command response mapping", () => {
	it("maps known custom and built-in commands", () => {
		expect(toSlashCommand(response())?.source).toBe("custom");
		expect(toSlashCommand(response({ id: null, name: "ping", source: "builtIn" }))?.source).toBe("builtIn");
	});

	it("fails closed for unknown action and source discriminants", () => {
		const unknownAction = {
			...response(),
			action: { type: "runTool", prompt: "unsafe" },
		} as unknown as XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse;
		expect(toSlashCommand(unknownAction)).toBeNull();
		expect(toSlashCommand(response({ source: "remote" }))).toBeNull();
	});

	it("fails closed when source and identity disagree and filters invalid catalog entries", () => {
		expect(toSlashCommand(response({ id: "unexpected", source: "builtIn" }))).toBeNull();
		expect(toSlashCommand(response({ id: null, source: "custom" }))).toBeNull();
		expect(toSlashCommands([response(), response({ source: "remote" })])).toHaveLength(1);
	});
});
