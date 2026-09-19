import { Badge, Box, Button, Container, Group, Radio, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getNodeSettingsQueryKey, saveNodeSettingsMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import type { UiMode } from "@/capabilities/NodeCapabilities";

// The first-run navigation-mode choice, shown once after the external-access step. Like that screen it has no skip
// control — the layout guard sends anyone with an unanswered mode straight back here, so a way out that did not save
// would only produce a loop.
//
// NEITHER option is preselected: this is a deliberate first-run decision, so the operator has to make it rather than
// confirm one the screen made for them. "Simple" carries the recommendation as a badge instead, which is how the
// external-access screen marks its own favoured choice.
export function UiModeSetup() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const queryClient = useQueryClient();
	const [choice, setChoice] = useState<UiMode | null>(null);

	const saveMutation = useMutation(withResponseValidation(saveNodeSettingsMutation()));

	const submit = (): void => {
		if (choice === null) {
			return;
		}

		saveMutation.mutate(
			{ body: { uiMode: choice } },
			{
				onSuccess: async (saved) => {
					// Seed, do not invalidate — the same reason as ExternalAccessSetup: this page mounts no node-settings
					// query, so an invalidation would mark the entry stale without refetching it and the layout guard's
					// `ensureQueryData` would read back the null mode its own beforeLoad had cached, bouncing us right back.
					queryClient.setQueryData(getNodeSettingsQueryKey(), saved);
					await navigate({ to: "/" });
				},
			},
		);
	};

	const isSaving = saveMutation.isPending;

	return (
		<Box pos="relative">
			<Box pos="absolute" top={16} right={16} style={{ zIndex: 10 }}>
				<LanguageMenu />
			</Box>
			<Container size="sm" py="xl" className="min-h-dvh flex items-center">
				<Stack gap="lg" className="w-full">
					<Stack gap={4} align="center" ta="center">
						<Title order={1}>{t("pages.uiMode.setupTitle", "How much of the engine do you want to see?")}</Title>
						<Text c="dimmed">
							{t("pages.uiMode.setupSubtitle", "Nothing is deleted either way, and you can change this later in Node settings.")}
						</Text>
					</Stack>

					{saveMutation.isError ? (
						<InlineErrorAlert
							message={apiErrorMessage(saveMutation.error, t("pages.uiMode.saveError", "Could not save your choice. Try again."))}
							data-testid="ui-mode-save-error"
						/>
					) : null}

					{/* Radio.Card rather than two buttons: the two options are one question with one answer, so a radio group
					    is what a screen reader should announce, and it is arrow-key navigable with a visible focus ring. */}
					<Radio.Group
						value={choice ?? ""}
						onChange={(value) => setChoice(value === "simple" ? "simple" : "advanced")}
						aria-label={t("pages.uiMode.setupTitle", "How much of the engine do you want to see?")}
					>
						<Stack gap="md">
							<Radio.Card value="simple" radius="lg" p="xl" data-testid="ui-mode-simple-card">
								<Group align="flex-start" wrap="nowrap" gap="md">
									<Radio.Indicator />
									<Stack gap="xs">
										<Group gap="sm" align="center">
											<Title order={2} size="h3">
												{t("pages.uiMode.simple.title", "Simple")}
											</Title>
											<Badge variant="light">{t("pages.uiMode.simple.badge", "Recommended for most people")}</Badge>
										</Group>
										<Text>
											{t(
												"pages.uiMode.simple.body",
												"Chat, agents, models, knowledge, images and transcription. The building and monitoring tools stay out of the way.",
											)}
										</Text>
									</Stack>
								</Group>
							</Radio.Card>

							<Radio.Card value="advanced" radius="lg" p="xl" data-testid="ui-mode-advanced-card">
								<Group align="flex-start" wrap="nowrap" gap="md">
									<Radio.Indicator />
									<Stack gap="xs">
										<Title order={2} size="h3">
											{t("pages.uiMode.advanced.title", "Advanced")}
										</Title>
										<Text>
											{t(
												"pages.uiMode.advanced.body",
												"Everything, including tool and skill authoring, scheduling, workflows, integrations, benchmarks and training.",
											)}
										</Text>
									</Stack>
								</Group>
							</Radio.Card>
						</Stack>
					</Radio.Group>

					<Button
						fullWidth={true}
						loading={isSaving}
						disabled={choice === null || isSaving}
						onClick={submit}
						data-testid="ui-mode-continue"
					>
						{t("pages.uiMode.continue", "Continue")}
					</Button>
				</Stack>
			</Container>
		</Box>
	);
}
