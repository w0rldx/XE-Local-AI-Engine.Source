import type { SlashCommand } from "@/features/commands/models/CommandModels";

export interface ChatCommandOption {
	readonly id: string | null;
	readonly name: string;
	readonly description: string | null;
	readonly prompt: string;
}

export function toChatCommandOption(command: SlashCommand): ChatCommandOption {
	return { id: command.id, name: command.name, description: command.description, prompt: command.action.prompt };
}
