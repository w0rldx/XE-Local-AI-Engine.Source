import { ActionIcon, Menu, Select, Slider, Stack, Switch, Text, Tooltip } from "@mantine/core";
import { IconVolume, IconVolumeOff } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { useVoicePreferencesStore, voicePreferencesRateBounds } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";
import { useWebSpeechVoices } from "@/features/voice/WebSpeechVoiceCatalog";

// Composer voice controls: on/off toggle + (when on) a settings menu with the voice-profile picker, autoplay switch,
// and speaking-rate slider. Self-gates on the runtime context `enabled` flag (developer mode + node setting) so it
// renders nothing when voice is unavailable. Voice choices come from the browser/OS Web Speech catalog.

export function VoiceComposerControls() {
	const { t } = useTranslation();
	const { enabled, defaultVoiceProfile } = useVoiceRuntime();
	const osVoices = useWebSpeechVoices();
	const voiceEnabled = useVoicePreferencesStore((state) => state.voiceEnabled);
	const voiceProfile = useVoicePreferencesStore((state) => state.voiceProfile);
	const speakingRate = useVoicePreferencesStore((state) => state.speakingRate);
	const autoPlayAssistant = useVoicePreferencesStore((state) => state.autoPlayAssistant);
	const { toggleVoiceEnabled, setVoiceProfile, setSpeakingRate, setAutoPlayAssistant } = useVoicePreferencesStore(
		(state) => state.actions,
	);

	const voiceOptions = useMemo(
		() => [...osVoices.values()].flatMap((voices) => voices.map((voice) => ({ value: voice.id, label: voice.name }))),
		[osVoices],
	);

	if (!enabled) {
		return null;
	}

	const selectedProfile = voiceProfile || defaultVoiceProfile || null;

	return (
		<Menu position="top-start" offset={8} withinPortal={true} closeOnItemClick={false}>
			<Menu.Target>
				<Tooltip label={voiceEnabled ? t("voice.composer.enabledTooltip") : t("voice.composer.disabledTooltip")}>
					<ActionIcon
						size={36}
						variant={voiceEnabled ? "light" : "subtle"}
						color={voiceEnabled ? "primary" : "gray"}
						aria-label={t("voice.composer.label")}
						aria-pressed={voiceEnabled}
						data-testid="voice-composer-trigger"
					>
						{voiceEnabled ? <IconVolume size={15} /> : <IconVolumeOff size={15} />}
					</ActionIcon>
				</Tooltip>
			</Menu.Target>
			<Menu.Dropdown>
				<Stack gap="sm" p="xs" style={{ minWidth: 240 }}>
					<Switch
						label={t("voice.composer.enableLabel")}
						checked={voiceEnabled}
						onChange={() => toggleVoiceEnabled()}
						data-testid="voice-enable-switch"
					/>
					<Switch
						label={t("voice.composer.autoPlayLabel")}
						checked={autoPlayAssistant}
						disabled={!voiceEnabled}
						onChange={(event) => setAutoPlayAssistant(event.currentTarget.checked)}
						data-testid="voice-autoplay-switch"
					/>
					<Select
						label={t("voice.composer.profileLabel")}
						data={voiceOptions}
						value={selectedProfile}
						disabled={!voiceEnabled || voiceOptions.length === 0}
						onChange={(value) => setVoiceProfile(value ?? "")}
						comboboxProps={{ withinPortal: true }}
						data-testid="voice-profile-select"
					/>
					<Stack gap={2}>
						<Text size="sm">{t("voice.composer.rateLabel")}</Text>
						<Slider
							min={voicePreferencesRateBounds.min}
							max={voicePreferencesRateBounds.max}
							step={0.1}
							value={speakingRate}
							disabled={!voiceEnabled}
							onChange={setSpeakingRate}
							label={(value) => `${value.toFixed(1)}×`}
							data-testid="voice-rate-slider"
						/>
					</Stack>
				</Stack>
			</Menu.Dropdown>
		</Menu>
	);
}
