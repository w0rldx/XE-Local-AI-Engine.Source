import type { CommandSource, SlashCommand } from "@/core/commands/models/CommandModels";

export interface ChatCommandOption {
	readonly id: string | null;
	readonly name: string;
	readonly description: string | null;
	readonly prompt: string;
	readonly source: CommandSource;
}

export function toChatCommandOption(command: SlashCommand): ChatCommandOption {
	return {
		id: command.id,
		name: command.name,
		description: command.description,
		prompt: command.action.prompt,
		source: command.source,
	};
}
