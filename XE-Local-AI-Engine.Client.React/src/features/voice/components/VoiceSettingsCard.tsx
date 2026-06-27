import { Alert, Badge, Card, Group, Select, Slider, Stack, Switch, Text, Title } from "@mantine/core";
import { IconInfoCircle, IconVolume } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { VoicePreviewButton } from "@/features/voice/components/VoicePreviewButton";
import { useVoiceNodeSettings } from "@/features/voice/useVoiceNodeSettings";
import { useVoicePreferencesStore, voicePreferencesRateBounds } from "@/features/voice/VoicePreferencesStore";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

// Node Settings voice block. Lets the operator drive the node-level voice feature through the existing
// operator-gated node-settings GET/PUT (the master gate `voiceFeatureEnabled` — which composes server-side into the
// manifest's `enabled` — plus the `defaultVoiceProfile`), and lets each user manage the per-browser client prefs
// (master enable, autoplay, profile, speaking rate). Saving a node field invalidates the voice manifest so the
// surface gate below re-evaluates immediately.

export function VoiceSettingsCard() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	// Reuse the single manifest fetched by the app-root ClientAiRuntimeProvider (no duplicate query). The provider
	// fetches it only under developer mode, which matches this card's own gate.
	const { manifest } = useVoiceRuntime();
	// Operator-owned node settings (read + write the node-level voice fields via the existing node-settings endpoint).
	const nodeVoice = useVoiceNodeSettings(developerMode);

	const voiceEnabled = useVoicePreferencesStore((state) => state.voiceEnabled);
	const voiceProfile = useVoicePreferencesStore((state) => state.voiceProfile);
	const speakingRate = useVoicePreferencesStore((state) => state.speakingRate);
	const autoPlayAssistant = useVoicePreferencesStore((state) => state.autoPlayAssistant);
	const { setVoiceEnabled, setVoiceProfile, setSpeakingRate, setAutoPlayAssistant } = useVoicePreferencesStore(
		(state) => state.actions,
	);

	const voiceOptions = useMemo(
		() => (manifest?.voices ?? []).map((voice) => ({ value: voice.id, label: `${voice.name} (${voice.language})` })),
		[manifest?.voices],
	);

	if (!developerMode) {
		return null;
	}

	const operatorEnabled = manifest?.enabled === true;
	const selectedProfile = voiceProfile || manifest?.defaultVoiceId || null;
	const nodeDefaultProfile = nodeVoice.defaultVoiceProfile || manifest?.defaultVoiceId || null;

	const handleNodeGateChange = (checked: boolean): void => {
		nodeVoice.save(
			{ voiceFeatureEnabled: checked },
			{ onError: () => toast.error(t("voice.settings.operatorSaveError")) },
		);
	};

	const handleNodeDefaultProfileChange = (value: string | null): void => {
		if (!value) {
			return;
		}
		nodeVoice.save(
			{ defaultVoiceProfile: value },
			{ onError: () => toast.error(t("voice.settings.operatorSaveError")) },
		);
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="voice-settings-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={3}>{t("voice.settings.title")}</Title>
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
						checked={nodeVoice.voiceFeatureEnabled}
						disabled={nodeVoice.isLoading || nodeVoice.isSaving}
						onChange={(event) => handleNodeGateChange(event.currentTarget.checked)}
						data-testid="voice-settings-node-gate-switch"
					/>
					<Group align="flex-end" gap="xs" wrap="nowrap">
						<Select
							label={t("voice.settings.operatorDefaultProfileLabel")}
							description={t("voice.settings.operatorDefaultProfileDescription")}
							data={voiceOptions}
							value={nodeDefaultProfile}
							disabled={voiceOptions.length === 0 || nodeVoice.isSaving}
							onChange={handleNodeDefaultProfileChange}
							data-testid="voice-settings-node-default-profile"
							style={{ flex: 1 }}
						/>
						<VoicePreviewButton voiceId={nodeDefaultProfile} />
					</Group>
				</Stack>

				{operatorEnabled ? (
					<Stack gap="sm">
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
