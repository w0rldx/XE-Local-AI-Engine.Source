import { Stack } from "@mantine/core";
import { memo } from "react";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import { ThoughtsSection } from "@/features/chat/components/ThoughtsSection";
import { ToolCallCard } from "@/features/chat/components/ToolCallCard";
import type { ChatMessagePart } from "@/features/chat/models/ChatModels";

interface MessagePartsProps {
	parts: ChatMessagePart[];
	// True while the turn is in flight. Only the trailing reasoning segment of a live turn streams; earlier
	// reasoning runs (before a tool) are complete and render as folded Thoughts blocks.
	isStreaming?: boolean;
	streamingReasoningOverflowBytes?: number;
	// True once the answer body has begun; hides the live reasoning timer (matches the pre-interleave behavior).
	hasContentStarted?: boolean;
	// Active composer reasoning effort, forwarded so a folded segment can flag reasoning emitted while "none" is set.
	reasoningBypassed?: boolean;
}

/** Index of the last reasoning part — the only segment that streams live while the turn is in flight. */
function lastReasoningIndex(parts: ChatMessagePart[]): number {
	for (let index = parts.length - 1; index >= 0; index -= 1) {
		if (parts[index]?.kind === "reasoning") {
			return index;
		}
	}

	return -1;
}

/**
 * Renders the assistant turn's ordered interleave (reasoning → tool → reasoning → …) in `sequence` order: each
 * reasoning run as its own folded `ThoughtsSection`, each tool as a state-driven `ToolCallCard`, and any mid-turn
 * text as markdown. The single source of truth shared by the live stream and the post-reload render.
 */
export const MessageParts = memo(function MessageParts({
	parts,
	isStreaming = false,
	streamingReasoningOverflowBytes = 0,
	hasContentStarted = false,
	reasoningBypassed = false,
}: MessagePartsProps) {
	if (parts.length === 0) {
		return null;
	}

	const liveReasoningIndex = isStreaming ? lastReasoningIndex(parts) : -1;

	// One Stack owns the spacing between every part so reasoning↔tool↔reasoning gaps stay uniform (the blocks
	// themselves carry no bottom margin — see ThoughtsSection.module.css).
	return (
		<Stack gap="sm">
			{parts.map((part, index) => {
				if (part.kind === "reasoning") {
					const isLive = index === liveReasoningIndex;
					return (
						<ThoughtsSection
							key={part.id}
							messageId={part.id}
							reasoning={isLive ? undefined : part.text}
							streamingContent={isLive ? part.text : undefined}
							streamingOverflowBytes={isLive ? streamingReasoningOverflowBytes : 0}
							isStreaming={isLive}
							hasContentStarted={hasContentStarted}
							reasoningBypassed={reasoningBypassed}
						/>
					);
				}

				if (part.kind === "tool") {
					return <ToolCallCard key={part.id} part={part} />;
				}

				return <ChatMarkdown key={part.id} content={part.text} />;
			})}
		</Stack>
	);
});
