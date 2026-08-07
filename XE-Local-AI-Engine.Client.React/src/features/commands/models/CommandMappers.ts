import type {
	XeLocalAiEngineClientEndpointsAutomationV1CreateSlashCommandRequest,
	XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse,
} from "@/core/api/generated";
import type { CommandFormValues, SlashCommand } from "@/features/commands/models/CommandModels";


export function toSlashCommand(command: XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse): SlashCommand | null {
	if (command.action.type !== "sendPrompt") {
		return null;
	}
	if (command.source !== "builtIn" && command.source !== "custom") {
		return null;
	}
	if ((command.source === "builtIn" && command.id != null) || (command.source === "custom" && !command.id)) {
		return null;
	}
	return {
		id: command.id ?? null,
		name: (command.name ?? "").trim().toLowerCase(),
		description: command.description?.trim() || null,
		source: command.source,
		action: { type: "SendPrompt", prompt: command.action.prompt },
	};
}

export function toSlashCommands(
	commands: readonly XeLocalAiEngineClientEndpointsAutomationV1SlashCommandResponse[],
): SlashCommand[] {
	return commands.flatMap((command) => {
		const mapped = toSlashCommand(command);
		return mapped ? [mapped] : [];
	});
}

export function toSaveCommandRequest(
	values: CommandFormValues,
): XeLocalAiEngineClientEndpointsAutomationV1CreateSlashCommandRequest {
	return {
		name: values.name.trim().toLowerCase(),
		description: values.description.trim() || null,
		action: { type: "sendPrompt", prompt: values.prompt.trim() },
	};
}
