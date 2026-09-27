import {
	Alert,
	Badge,
	Card,
	type ComboboxItem,
	type ComboboxItemGroup,
	Group,
	Select,
	Slider,
	Stack,
	Switch,
	Text,
	Title,
} from "@mantine/core";
import { IconInfoCircle, IconVolume } from "@tabler/icons-react";
import { type ReactNode, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { VoicePreviewButton } from "@/features/voice/components/VoicePreviewButton";
import { useVoiceNodeSettings } from "@/features/voice/useVoiceNodeSettings";
import { useVoicePreferencesStore, voicePreferencesRateBounds } from "@/features/voice/VoicePreferencesStore";
import { type OsVoiceCatalog, useWebSpeechVoices } from "@/features/voice/WebSpeechVoiceCatalog";

/** Best-effort human language name for a short IETF code (e.g. "de" → "German"), falling back to the bare code
 * when the runtime has no `Intl.DisplayNames` data for it. */
function languageDisplayName(code: string, uiLocale: string): string {
	try {
		return new Intl.DisplayNames([uiLocale], { type: "language" }).of(code) ?? code;
	} catch {
		return code;
	}
}

/** Builds the grouped Select data from every voice installed in the browser/operating system. */
function buildVoiceGroups(
	osVoices: OsVoiceCatalog,
	uiLocale: string,
	systemGroupLabel: (language: string) => string,
): ComboboxItemGroup<ComboboxItem>[] {
	const groups: ComboboxItemGroup<ComboboxItem>[] = [];
	const languages = [...osVoices.keys()].sort((a, b) =>
		languageDisplayName(a, uiLocale).localeCompare(languageDisplayName(b, uiLocale)),
	);
	for (const language of languages) {
		const entries = osVoices.get(language) ?? [];
		groups.push({
			group: systemGroupLabel(languageDisplayName(language, uiLocale)),
			items: entries.map((entry) => ({ value: entry.id, label: entry.name })),
		});
	}

	return groups;
}

interface VoiceSettingsCardProps {
	// The node-level voice fields are DRAFT values owned by the Node settings page and persisted by its save bar. The
	// saved gate (read here through useVoiceNodeSettings) still decides whether the per-browser controls are offered,
	// because the voice runtime follows the saved state, not the draft.
	readonly voiceFeatureEnabled: boolean;
	readonly defaultVoiceProfile: string;
	readonly onVoiceFeatureEnabledChange: (enabled: boolean) => void;
	readonly onDefaultVoiceProfileChange: (profile: string) => void;
	// Rendered next to the per-browser block's heading (the page's "this browser only" marker).
	readonly browserOnlyBadge?: ReactNode;
}

// Node Settings voice block. The operator edits the node-level voice feature (the master gate `voiceFeatureEnabled`
// plus the legacy-compatible `defaultVoiceProfile`) as part of the page draft, and each user manages the per-browser
// client prefs (master enable, autoplay, profile, speaking rate), which apply instantly. Voice choices come only from
// the browser/OS Web Speech catalog.
export function VoiceSettingsCard({
	voiceFeatureEnabled,
	defaultVoiceProfile,
	onVoiceFeatureEnabledChange,
	onDefaultVoiceProfileChange,
	browserOnlyBadge,
}: VoiceSettingsCardProps) {
	const { t, i18n } = useTranslation();
	// The saved node state: the per-browser block follows it, since the runtime only starts once the gate is saved.
	const nodeVoice = useVoiceNodeSettings();
	// Every OS/browser voice, grouped by language.
	const osVoices = useWebSpeechVoices();

	const voiceEnabled = useVoicePreferencesStore((state) => state.voiceEnabled);
	const voiceProfile = useVoicePreferencesStore((state) => state.voiceProfile);
	const speakingRate = useVoicePreferencesStore((state) => state.speakingRate);
	const autoPlayAssistant = useVoicePreferencesStore((state) => state.autoPlayAssistant);
	const { setVoiceEnabled, setVoiceProfile, setSpeakingRate, setAutoPlayAssistant } = useVoicePreferencesStore(
		(state) => state.actions,
	);

	const voiceOptions = useMemo(
		() => buildVoiceGroups(osVoices, i18n.language, (language) => t("voice.settings.systemVoiceGroupLabel", { language })),
		[osVoices, i18n.language, t],
	);

	const operatorEnabled = nodeVoice.voiceFeatureEnabled;
	const selectedProfile = voiceProfile || nodeVoice.defaultVoiceProfile || null;
	const nodeDefaultProfile = defaultVoiceProfile === "" ? null : defaultVoiceProfile;

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="voice-settings-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h3">
						{t("voice.settings.title")}
					</Title>
					<IconVolume size={22} />
				</Group>
				<Group gap="xs">
					<Text c="dimmed" size="sm">
						{t("voice.settings.operatorGate")}
					</Text>
					<Badge color={operatorEnabled ? "teal" : "gray"} variant="light">
						{operatorEnabled ? t("voice.settings.gateOn") : t("voice.settings.gateOff")}
					</Badge>
				</Group>

				<Stack gap="sm">
					<Text c="dimmed" size="sm" fw={600}>
						{t("voice.settings.operatorSectionTitle")}
					</Text>
					<Switch
						label={t("voice.settings.operatorEnableLabel")}
						description={t("voice.settings.operatorEnableDescription")}
						checked={voiceFeatureEnabled}
						disabled={nodeVoice.isLoading}
						onChange={(event) => onVoiceFeatureEnabledChange(event.currentTarget.checked)}
						data-testid="voice-settings-node-gate-switch"
					/>
					<Group align="flex-end" gap="xs" wrap="nowrap">
						<Select
							label={t("voice.settings.operatorDefaultProfileLabel")}
							description={t("voice.settings.operatorDefaultProfileDescription")}
							data={voiceOptions}
							value={nodeDefaultProfile}
							disabled={voiceOptions.length === 0}
							allowDeselect={false}
							onChange={(value) => {
								if (value) {
									onDefaultVoiceProfileChange(value);
								}
							}}
							data-testid="voice-settings-node-default-profile"
							style={{ flex: 1 }}
						/>
						<VoicePreviewButton voiceId={nodeDefaultProfile} />
					</Group>
				</Stack>

				{operatorEnabled ? (
					<Stack gap="sm">
						<Group gap="xs">
							<Text c="dimmed" size="sm" fw={600}>
								{t("voice.settings.browserSectionTitle")}
							</Text>
							{browserOnlyBadge}
						</Group>
						<Switch
							label={t("voice.settings.enableLabel")}
							description={t("voice.settings.enableDescription")}
							checked={voiceEnabled}
							onChange={(event) => setVoiceEnabled(event.currentTarget.checked)}
							data-testid="voice-settings-enable-switch"
						/>
						<Switch
							label={t("voice.settings.autoPlayLabel")}
							checked={autoPlayAssistant}
							disabled={!voiceEnabled}
							onChange={(event) => setAutoPlayAssistant(event.currentTarget.checked)}
						/>
						<Group align="flex-end" gap="xs" wrap="nowrap">
							<Select
								label={t("voice.settings.profileLabel")}
								data={voiceOptions}
								value={selectedProfile}
								disabled={voiceOptions.length === 0}
								onChange={(value) => setVoiceProfile(value ?? "")}
								style={{ flex: 1 }}
							/>
							<VoicePreviewButton voiceId={selectedProfile} />
						</Group>
						<Stack gap={2}>
							<Text size="sm">{t("voice.settings.rateLabel")}</Text>
							<Slider
								min={voicePreferencesRateBounds.min}
								max={voicePreferencesRateBounds.max}
								step={0.1}
								value={speakingRate}
								onChange={setSpeakingRate}
								label={(value) => `${value.toFixed(1)}×`}
							/>
						</Stack>
					</Stack>
				) : (
					<Alert color="gray" variant="light" icon={<IconInfoCircle size={16} />}>
						<Text size="sm">{t("voice.settings.disabledOnNode")}</Text>
					</Alert>
				)}
			</Stack>
		</Card>
	);
}
