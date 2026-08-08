import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";

export function slashCommandOptionId(name: string): string {
	return `slash-command-option-${name}`;
}

interface SlashTriggerInput {
	content: string;
	selectionStart: number;
	selectionEnd: number;
	interactive: boolean;
	isComposing: boolean;
}

export function getSlashCommandQuery(input: SlashTriggerInput): string | null {
	if (!input.interactive || input.isComposing || input.selectionStart !== input.selectionEnd || input.selectionEnd !== input.content.length) {
		return null;
	}
	const match = /^\/([^\s/]*)$/.exec(input.content);
	return match?.[1]?.toLowerCase() ?? null;
}

function rank(option: ChatCommandOption, query: string): number | null {
	const name = option.name.toLowerCase();
	const description = option.description?.toLowerCase() ?? "";
	if (!query) {
		return 4;
	}
	if (name === query) {
		return 0;
	}
	if (name.startsWith(query)) {
		return 1;
	}
	if (name.includes(query)) {
		return 2;
	}
	if (description.includes(query)) {
		return 3;
	}
	return null;
}

function compareOrdinal(left: string, right: string): number {
	return left < right ? -1 : left > right ? 1 : 0;
}

export function matchSlashCommands(options: readonly ChatCommandOption[], query: string): ChatCommandOption[] {
	return options
		.map((option) => ({ option, rank: rank(option, query) }))
		.filter((candidate): candidate is { option: ChatCommandOption; rank: number } => candidate.rank !== null)
		.sort((left, right) => left.rank - right.rank || compareOrdinal(left.option.name, right.option.name))
		.map((candidate) => candidate.option);
}

export function resolveSlashCommand(content: string, options: readonly ChatCommandOption[]): ChatCommandOption | null {
	const match = /^\/([a-z0-9]+(?:-[a-z0-9]+)*)$/.exec(content);
	if (!match?.[1]) {
		return null;
	}
	const name = match[1].toLowerCase();
	return options.find((option) => option.name === name) ?? null;
}

export function slashInputSignature(content: string, selectionStart: number, selectionEnd: number): string {
	return `${selectionStart}:${selectionEnd}:${content}`;
}
