import { Box, Button, Container, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getDevelopmentCapabilityOptions,
	getNodeSettingsQueryKey,
	saveNodeSettingsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { StandaloneScreenControls } from "@/core/ui/components/StandaloneScreenControls/StandaloneScreenControls";
import {
	type SandboxSecurityProfile,
	SandboxProfileRadioCards,
} from "@/features/node-settings/components/SandboxProfileRadioCards";

// The first-run sandbox security profile choice, between the external-access step and the interface mode. Like those
// screens it has no skip control and preselects nothing: the layout guard sends a node with a pending profile straight
// back here, and this is a security decision the operator makes rather than confirms.
//
// The recommendation follows the host: High when it would refuse nothing here, Low otherwise. When the capability read
// fails the page still offers both cards, just without a badge, because a missing recommendation must not block the step.
export function SandboxProfileSetup() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const queryClient = useQueryClient();
	const [choice, setChoice] = useState<SandboxSecurityProfile | null>(null);

	const capabilityQuery = useQuery(withResponseValidation(getDevelopmentCapabilityOptions()));
	const refusals = capabilityQuery.data?.highProfileRefusals;
	const recommended: SandboxSecurityProfile | null = refusals === undefined ? null : refusals.length === 0 ? "high" : "low";

	const saveMutation = useMutation(withResponseValidation(saveNodeSettingsMutation()));

	const submit = (): void => {
		if (choice === null) {
			return;
		}

		saveMutation.mutate(
			{ body: { sandboxSecurityProfile: choice } },
			{
				onSuccess: async (saved) => {
					// Seed, do not invalidate, for the reason UiModeSetup gives: an invalidation would leave the layout guard
					// reading back the cached "pending" and bouncing us straight back here.
					queryClient.setQueryData(getNodeSettingsQueryKey(), saved);
					await navigate({ to: "/" });
				},
			},
		);
	};

	const isSaving = saveMutation.isPending;

	return (
		<Box pos="relative">
			<StandaloneScreenControls />
			<Container size="sm" py="xl" className="min-h-dvh flex items-center">
				<Stack gap="lg" className="w-full">
					<Stack gap={4} align="center" ta="center">
						<Title order={1}>{t("pages.sandboxProfile.setupTitle", "How strict should the sandbox be?")}</Title>
						<Text c="dimmed">
							{t(
								"pages.sandboxProfile.setupSubtitle",
								"This decides what happens when this host cannot isolate a workload the way it asks to be isolated. You can change it later in Node settings.",
							)}
						</Text>
					</Stack>

					{saveMutation.isError ? (
						<InlineErrorAlert
							message={apiErrorMessage(
								saveMutation.error,
								t("pages.sandboxProfile.saveError", "Could not save your choice. Try again."),
							)}
							data-testid="sandbox-profile-save-error"
						/>
					) : null}

					<SandboxProfileRadioCards
						value={choice}
						onChange={setChoice}
						recommended={recommended}
						highRefusals={refusals}
						testIdPrefix="sandbox-profile"
					/>

					<Text size="sm" c="dimmed" ta="center">
						{t("pages.sandboxProfile.previewsNote", "Execution previews stay a separate setting: neither profile turns them on.")}
					</Text>

					<Button
						fullWidth={true}
						loading={isSaving}
						disabled={choice === null || isSaving}
						onClick={submit}
						data-testid="sandbox-profile-continue"
					>
						{t("pages.sandboxProfile.continue", "Continue")}
					</Button>
				</Stack>
			</Container>
		</Box>
	);
}
