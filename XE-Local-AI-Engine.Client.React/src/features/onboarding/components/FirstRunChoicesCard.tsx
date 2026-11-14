import { Affix, Button, CloseButton, Group, Paper, SegmentedControl, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { UiMode } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getNodeSettingsOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { type TutorialStatus, useTutorialState } from "@/core/onboarding/hooks/useTourState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import { useVisibleAssistantReplyCount } from "@/features/onboarding/hooks/useVisibleAssistantReplyCount";

// Recorded per user in the tutorial state, beside the tours: either answer hides the card for good.
const FIRST_RUN_CHOICES_KEY = "first-run-choices-v1";

type SandboxProfile = "low" | "high";

/**
 * The two choices the first run no longer asks for, offered once after the first answer. Owed only while the sandbox
 * profile is still "pending", which only a node set up with the defaults holds, so an install that already chose never
 * sees it. `idle` is the provider's "no other overlay is open" (acknowledgement, welcome dialog, running tour).
 */
export function FirstRunChoicesCard({ idle }: { readonly idle: boolean }) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	const settings = useQuery({ ...getNodeSettingsOptions(), enabled: isAuthenticated });
	const tutorialState = useTutorialState();
	const [closed, setClosed] = useState(false);
	const [uiMode, setUiMode] = useState<UiMode | null>(null);
	const [profile, setProfile] = useState<SandboxProfile>("low");
	const save = useMutation(saveNodeSettingsMutation());

	const owed =
		idle &&
		!closed &&
		tutorialState.isSuccess &&
		tutorialState.statusByKey[FIRST_RUN_CHOICES_KEY] === undefined &&
		settings.data?.sandboxSecurityProfile === "pending";
	const replyCount = useVisibleAssistantReplyCount(queryClient, owed);
	if (!owed || replyCount === 0) {
		return null;
	}

	// As useUiMode reads it: an unset mode (a node upgraded part-way through first run) runs as Advanced.
	const savedMode: UiMode = settings.data?.uiMode === "simple" ? "simple" : "advanced";
	const currentMode: UiMode = uiMode ?? savedMode;
	const close = (status: TutorialStatus): void => {
		setClosed(true);
		tutorialState.markDone(FIRST_RUN_CHOICES_KEY, status, { onError: () => toast.error(t("onboarding.errors.saveState")) });
	};
	const submit = (): void => {
		save.mutate(
			{ body: { uiMode: currentMode, sandboxSecurityProfile: profile } },
			{
				onSuccess: (saved) => {
					queryClient.setQueryData(getNodeSettingsQueryKey(), saved);
					close("completed");
				},
			},
		);
	};

	return (
		<Affix position={{ top: 72, right: 16 }}>
			<Paper
				withBorder={true}
				shadow="md"
				radius="lg"
				p="md"
				w="min(380px, calc(100vw - 32px))"
				role="region"
				aria-label={t("onboarding.firstRunChoices.title", "Two settings you can change")}
				data-testid="first-run-choices-card"
			>
				<Stack gap="sm">
					<Group justify="space-between" wrap="nowrap" align="flex-start">
						<Title order={2} size="h4">
							{t("onboarding.firstRunChoices.title", "Two settings you can change")}
						</Title>
						<CloseButton
							onClick={() => close("skipped")}
							aria-label={t("onboarding.actions.notNow", "Not now")}
							data-testid="first-run-choices-close"
						/>
					</Group>
					<Text size="sm" c="dimmed">
						{savedMode === "simple"
							? t(
									"onboarding.firstRunChoices.body",
									"The engine started in Simple mode with the Low sandbox profile. Change either now, or later in Node settings.",
								)
							: t(
									"onboarding.firstRunChoices.bodyAdvanced",
									"The engine runs in Advanced mode with the Low sandbox profile. Change either now, or later in Node settings.",
								)}
					</Text>

					<Text size="sm" fw={500}>
						{t("pages.nodeSettings.uiMode.title", "Interface mode")}
					</Text>
					<SegmentedControl
						value={currentMode}
						onChange={(value) => setUiMode(value === "advanced" ? "advanced" : "simple")}
						aria-label={t("pages.nodeSettings.uiMode.title", "Interface mode")}
						data={[
							{ value: "simple", label: t("pages.uiMode.simple.title", "Simple") },
							{ value: "advanced", label: t("pages.uiMode.advanced.title", "Advanced") },
						]}
						data-testid="first-run-choices-ui-mode"
					/>

					<Text size="sm" fw={500}>
						{t("pages.nodeSettings.sandboxProfile.title", "Sandbox security profile")}
					</Text>
					<SegmentedControl
						value={profile}
						onChange={(value) => setProfile(value === "high" ? "high" : "low")}
						aria-label={t("pages.nodeSettings.sandboxProfile.title", "Sandbox security profile")}
						data={[
							{ value: "low", label: t("pages.sandboxProfile.low.title", "Low") },
							{ value: "high", label: t("pages.sandboxProfile.high.title", "High") },
						]}
						data-testid="first-run-choices-sandbox-profile"
					/>
					<Text size="xs" c="dimmed">
						{profile === "high"
							? t(
									"onboarding.firstRunChoices.highNote",
									"High refuses a workload this computer cannot fully isolate instead of running it with less. Node settings lists which ones.",
								)
							: t("onboarding.firstRunChoices.lowNote", "Low runs every workload with the isolation this computer can give it.")}
					</Text>

					{save.isError ? (
						<InlineErrorAlert
							message={apiErrorMessage(
								save.error,
								t("onboarding.firstRunChoices.saveError", "Could not save your choices. Try again."),
							)}
						/>
					) : null}

					<Group justify="flex-end" gap="xs">
						<Button variant="subtle" onClick={() => close("skipped")} data-testid="first-run-choices-not-now">
							{t("onboarding.actions.notNow", "Not now")}
						</Button>
						<Button onClick={submit} loading={save.isPending} data-testid="first-run-choices-save">
							{t("onboarding.firstRunChoices.save", "Save")}
						</Button>
					</Group>
				</Stack>
			</Paper>
		</Affix>
	);
}
