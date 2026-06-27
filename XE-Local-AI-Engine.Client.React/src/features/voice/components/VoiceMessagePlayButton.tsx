import { ActionIcon, Tooltip } from "@mantine/core";
import { IconPlayerPlayFilled, IconPlayerStopFilled } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ChatMessageModel } from "@/features/chat/models/ChatModels";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// Per-message Play/Stop affordance. Rendered inside the assistant turn's action row; it is
// self-gating, so it returns null unless voice is available (dev-gate + manifest) AND the user has voice on AND this
// is a terminal assistant turn with speakable content. Clicking Play halts any current playback then plays THIS
// message (runtime.speak does the barge-in); clicking Stop halts it. A new streaming turn / other Play resets the tag.
// The engine/language is chosen by playMessage from the SELECTED voice ("selected voice always wins"), so this
// button no longer guesses a language from the answer text.

interface VoiceMessagePlayButtonProps {
	readonly message: ChatMessageModel;
}

export function VoiceMessagePlayButton({ message }: VoiceMessagePlayButtonProps) {
	const { t } = useTranslation();
	const { enabled, playingMessageId, playMessage, stopPlayback } = useVoiceRuntime();
	const voiceEnabled = useVoicePreferencesStore((state) => state.voiceEnabled);

	const content = message.content.trim();
	// The action row only renders for terminal turns, but guard on role + content so the button never offers to speak
	// an empty/streaming/user message.
	if (!enabled || !voiceEnabled || message.role !== "assistant" || content.length === 0) {
		return null;
	}

	const isPlaying = playingMessageId === message.id;
	const label = isPlaying ? t("voice.message.stop") : t("voice.message.play");

	return (
		<Tooltip label={label} withArrow={true}>
			<ActionIcon
				aria-label={label}
				color={isPlaying ? "primary" : "gray"}
				variant="subtle"
				size="sm"
				onClick={() => {
					if (isPlaying) {
						stopPlayback();
						return;
					}

					playMessage(message.id, content).catch(() => undefined);
				}}
				data-testid={`voice-message-play-${message.id}`}
			>
				{isPlaying ? <IconPlayerStopFilled size={14} /> : <IconPlayerPlayFilled size={14} />}
			</ActionIcon>
		</Tooltip>
	);
}
