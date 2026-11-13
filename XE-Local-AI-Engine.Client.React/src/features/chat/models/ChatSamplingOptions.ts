import { type ChatSamplingOptions, toWireSamplingOptions, type WireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";

export * from "@/core/runtime/ChatSamplingOptions";

// The sampling a chat turn sends: the conversation's stored settings on every client, else this browser's defaults,
// which only developer mode can edit and so only developer mode applies.
export function turnSamplingOptions(
	conversationOptions: ChatSamplingOptions | undefined,
	browserDefaults: ChatSamplingOptions,
	developerMode: boolean,
): WireSamplingOptions | undefined {
	return toWireSamplingOptions(conversationOptions ?? (developerMode ? browserDefaults : {}));
}
