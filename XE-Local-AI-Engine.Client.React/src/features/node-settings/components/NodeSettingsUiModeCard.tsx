import { Group, SegmentedControl, Stack, Text, Title } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { IconLayoutSidebar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getNodeSettingsQueryKey, saveNodeSettingsMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useUiMode } from "@/core/layout/hooks/useUiMode";

// The home of the choice the first-run step promises can be changed here. Deliberately NOT part of the big fields
// form and its Save button: the mode only decides what the navigation renders, so it applies the moment it is picked.
// Seeding the node-settings cache with the save's own response is what makes the nav bars re-render on the next
// commit — they read the same query — so the rail changes under the operator without a reload.
export function NodeSettingsUiModeCard() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const uiMode = useUiMode();

	const saveMutation = useMutation({
		...withResponseValidation(saveNodeSettingsMutation()),
		onSuccess: (saved) => {
			queryClient.setQueryData(getNodeSettingsQueryKey(), saved);
		},
	});

	return (
		<SectionCard
			title={t("pages.nodeSettings.uiMode.title", "Interface mode")}
			icon={<IconLayoutSidebar size={22} />}
			data-testid="node-settings-ui-mode-card"
		>
			<Text c="dimmed">
				{t(
					"pages.nodeSettings.uiMode.description",
					"Simple shows the everyday pages only; Advanced shows every page this build offers. This changes the navigation and nothing else — every page stays reachable by its own address, and nothing is deleted.",
				)}
			</Text>

			{saveMutation.isError ? (
				<InlineErrorAlert
					message={apiErrorMessage(saveMutation.error, t("pages.uiMode.saveError", "Could not save your choice. Try again."))}
					data-testid="node-settings-ui-mode-error"
				/>
			) : null}

			<Group>
				<SegmentedControl
					value={uiMode}
					onChange={(value) => saveMutation.mutate({ body: { uiMode: value === "simple" ? "simple" : "advanced" } })}
					disabled={saveMutation.isPending}
					aria-label={t("pages.nodeSettings.uiMode.title", "Interface mode")}
					data={[
						{ value: "simple", label: t("pages.uiMode.simple.title", "Simple") },
						{ value: "advanced", label: t("pages.uiMode.advanced.title", "Advanced") },
					]}
					data-testid="node-settings-ui-mode-control"
				/>
			</Group>

			<Stack gap={2}>
				<Title order={3} size="h6">
					{uiMode === "simple" ? t("pages.uiMode.simple.title", "Simple") : t("pages.uiMode.advanced.title", "Advanced")}
				</Title>
				<Text size="sm" c="dimmed">
					{uiMode === "simple"
						? t(
								"pages.uiMode.simple.body",
								"Chat, agents, models, knowledge, images and transcription. The building and monitoring tools stay out of the way.",
							)
						: t(
								"pages.uiMode.advanced.body",
								"Everything, including tool and skill authoring, scheduling, workflows, integrations, benchmarks and training.",
							)}
				</Text>
			</Stack>
		</SectionCard>
	);
}
