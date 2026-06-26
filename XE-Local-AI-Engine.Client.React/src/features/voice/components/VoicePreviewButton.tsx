import { ActionIcon, Tooltip } from "@mantine/core";
import { IconPlayerPlayFilled } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// "Audition this voice" affordance for the Node Settings voice pickers. Clicking it barges in and speaks a short fixed
// sample with the given voice (runtime.previewVoice) so the user can hear a candidate before committing to it. It is
// self-gating: disabled until the runtime exists (operator gate on + capabilities probed) and a voice is selected. The
// busy state spans synthesis (which on the first English preview includes the one-time Kokoro model download).

interface VoicePreviewButtonProps {
	/** The voice to audition. Falsy → the button is inert (nothing selected yet). */
	readonly voiceId: string | null | undefined;
}

export function VoicePreviewButton({ voiceId }: VoicePreviewButtonProps) {
	const { t } = useTranslation();
	const { runtime, previewVoice } = useVoiceRuntime();
	const [isSynthesizing, setIsSynthesizing] = useState(false);

	const canPreview = Boolean(runtime) && Boolean(voiceId);
	const label = t("voice.settings.previewLabel", "Preview voice");

	const handlePreview = (): void => {
		if (!voiceId) {
			return;
		}

		setIsSynthesizing(true);
		previewVoice(voiceId)
			.catch(() => undefined)
			.finally(() => setIsSynthesizing(false));
	};

	return (
		<Tooltip label={label} withArrow={true}>
			<ActionIcon
				aria-label={label}
				variant="light"
				size="lg"
				disabled={!canPreview}
				loading={isSynthesizing}
				onClick={handlePreview}
				data-testid="voice-preview-button"
			>
				<IconPlayerPlayFilled size={16} />
			</ActionIcon>
		</Tooltip>
	);
}
