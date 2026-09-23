import { Box, Button, Card, Container, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getNodeSettingsQueryKey, saveNodeSettingsMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { StandaloneScreenControls } from "@/core/ui/components/StandaloneScreenControls/StandaloneScreenControls";
import type { ExternalAccessPreset } from "@/features/node-settings/models/NodeSettingsFieldsModel";

// The first-run external-access choice, shown once after setup and before the operator reaches the app. There is no
// skip control and no link home by design: the layout guard sends anyone with a "pending" profile straight back here,
// so a way out that did not save would only produce a loop. A failed save keeps BOTH choices enabled instead.
export function ExternalAccessSetup() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const queryClient = useQueryClient();

	const saveMutation = useMutation(withResponseValidation(saveNodeSettingsMutation()));

	const choose = (preset: ExternalAccessPreset): void => {
		// The profile NAME alone: the server mapper writes the preset's three switches. The client never sends them, and
		// never computes "custom".
		saveMutation.mutate(
			{ body: { externalAccessProfile: preset } },
			{
				onSuccess: async (saved) => {
					// Seed, do not invalidate. This page mounts no node-settings query, so an invalidation would mark the
					// entry stale without refetching it, and the layout guard's `ensureQueryData` would read back the
					// "pending" value its own beforeLoad had cached — bouncing the operator right back here.
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
						<Title order={1}>{t("pages.externalAccess.setupTitle", "Choose how this engine reaches the internet")}</Title>
						<Text c="dimmed">
							{t("pages.externalAccess.setupSubtitle", "Pick one now. You can change it later in Node settings.")}
						</Text>
					</Stack>

					{saveMutation.isError ? (
						<InlineErrorAlert
							message={apiErrorMessage(
								saveMutation.error,
								t("pages.externalAccess.saveError", "Could not save your choice. Try again."),
							)}
							data-testid="external-access-save-error"
						/>
					) : null}

					<Card withBorder={true} radius="lg" p="xl" data-testid="external-access-recommended-card">
						<Stack gap="md">
							<Title order={2} size="h3">
								{t("pages.externalAccess.recommended.title", "Recommended")}
							</Title>
							<Text>
								{t(
									"pages.externalAccess.recommended.body",
									"The engine checks for application updates and for llama.cpp runtime updates on its own, and downloads a starter model and runtime the first time it runs, so you can start chatting straight away.",
								)}
							</Text>
							<Button
								fullWidth={true}
								loading={isSaving}
								disabled={isSaving}
								onClick={() => choose("recommended")}
								data-testid="external-access-choose-recommended"
							>
								{t("pages.externalAccess.recommended.choose", "Use recommended")}
							</Button>
						</Stack>
					</Card>

					<Card withBorder={true} radius="lg" p="xl" data-testid="external-access-offline-card">
						<Stack gap="md">
							<Title order={2} size="h3">
								{t("pages.externalAccess.offline.title", "Offline / Manual")}
							</Title>
							<Text>
								{t(
									"pages.externalAccess.offline.disables",
									"Turns off three things the engine would otherwise do by itself: application update checks, llama.cpp runtime update checks, and the first-run model and runtime download. You can still start every one of them yourself, at any time.",
								)}
							</Text>
							{/* The honesty clause. It names what the profile does NOT block, and it stays in this card: the profile
							    is not a network switch, and saying otherwise would be a privacy misrepresentation. */}
							<Text c="dimmed">
								{t(
									"pages.externalAccess.offline.doesNotBlock",
									"This is not a network switch. It does not block the connection to the C0re platform, MCP servers you have configured, or model-catalog lookups.",
								)}
							</Text>
							<Button
								fullWidth={true}
								loading={isSaving}
								disabled={isSaving}
								onClick={() => choose("offline")}
								data-testid="external-access-choose-offline"
							>
								{t("pages.externalAccess.offline.choose", "Use offline / manual")}
							</Button>
						</Stack>
					</Card>
				</Stack>
			</Container>
		</Box>
	);
}
