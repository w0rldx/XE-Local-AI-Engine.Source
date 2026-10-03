import type { ChatStreamingState, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";

/**
 * The assistant message "Answer now" may target, or undefined to hide the button: the turn is live, its trailing
 * part is a reasoning segment with no answer text yet, and the model is one the backend arms for reasoning control
 * (thinking on, a llama.cpp model whose template ends its reasoning with a tag). The server still answers 409 when
 * the guess is wrong, for example on an agent that pins another model.
 */
export function answerNowMessageId(
	stream: ChatStreamingState | undefined,
	model: ModelOption | undefined,
	effort: ReasoningEffort,
): string | undefined {
	if (!stream?.isActive || model?.isReasoningControllable !== true || effort === "none") {
		return undefined;
	}

	const trailing = stream.parts?.at(-1);
	const reasoningStreaming = trailing ? trailing.kind === "reasoning" : (stream.reasoning ?? "").trim().length > 0;
	return reasoningStreaming && stream.content.trim().length === 0 ? stream.messageId : undefined;
}
