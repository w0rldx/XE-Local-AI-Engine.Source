import { z } from "zod";

export const COMMAND_NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
export const COMMAND_NAME_MAX = 64;
export const COMMAND_DESCRIPTION_MAX = 1024;
export const COMMAND_PROMPT_MAX = 20_000;
export const CUSTOM_COMMAND_CAPACITY = 100;

export type CommandSource = "builtIn" | "custom";
export interface CommandAction {
	readonly type: "SendPrompt";
	readonly prompt: string;
}

export interface SlashCommand {
	readonly id: string | null;
	readonly name: string;
	readonly description: string | null;
	readonly source: CommandSource;
	readonly action: CommandAction;
}

export interface CommandFormValues {
	name: string;
	description: string;
	actionType: "SendPrompt";
	prompt: string;
}

function utf8ByteLength(value: string): number {
	return new TextEncoder().encode(value.trim()).byteLength;
}

export const commandFormSchema = z.object({
	name: z.string().trim().toLowerCase().min(1).max(COMMAND_NAME_MAX).regex(COMMAND_NAME_PATTERN, { message: "commandNameInvalid" }),
	description: z.string().trim().max(COMMAND_DESCRIPTION_MAX).refine((value) => utf8ByteLength(value) <= COMMAND_DESCRIPTION_MAX, {
		message: "commandDescriptionTooLong",
	}),
	actionType: z.literal("SendPrompt"),
	prompt: z.string().trim().min(1).max(COMMAND_PROMPT_MAX).refine((value) => utf8ByteLength(value) <= COMMAND_PROMPT_MAX, {
		message: "commandPromptTooLong",
	}),
});
