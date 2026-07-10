import { Button, Group, Stack, Text, Title } from "@mantine/core";
import { IconCpu, IconExternalLink, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface RecommendationsHeaderProps {
	// Refresh-now fires the existing model-recommendation-check job; disabled when no such schedule exists.
	readonly canRefresh: boolean;
	readonly isRefreshing: boolean;
	readonly onRefresh: () => void;
	readonly onOpenScheduler: () => void;
	readonly onOpenModels: () => void;
}

// The advisor page's title block (eyebrow + title + subtitle) plus the top-level actions: cross-links to the Scheduler
// and Model Management pages and the Refresh-now button. Pure presentation — the parent owns navigation + refresh.
export function RecommendationsHeader({ canRefresh, isRefreshing, onRefresh, onOpenScheduler, onOpenModels }: RecommendationsHeaderProps) {
	const { t } = useTranslation();

	return (
		<Group justify="space-between" align="flex-start">
			<Stack gap={4}>
				<Text size="sm" tt="uppercase" fw={700} c="dimmed">
					{t("pages.modelFit.eyebrow", "Worker Node")}
				</Text>
				<Group gap="xs" align="center">
					<IconCpu size={24} />
					<Title order={2}>{t("pages.modelFit.recommendations.title", "Local model advisor")}</Title>
				</Group>
				<Text c="dimmed">
					{t(
						"pages.modelFit.recommendations.subtitle",
						"Hardware-aware local model guidance for this node. Detect hardware and pick the use case to see ranked, fit-checked model recommendations.",
					)}
				</Text>
			</Stack>
			<Group gap="sm">
				<Button
					variant="default"
					leftSection={<IconExternalLink size={16} />}
					onClick={onOpenScheduler}
					data-testid="model-fit-scheduler-link"
				>
					{t("pages.modelFit.recommendations.schedulerLink", "Scheduler")}
				</Button>
				<Button
					variant="default"
					leftSection={<IconExternalLink size={16} />}
					onClick={onOpenModels}
					data-testid="model-fit-models-link"
				>
					{t("pages.modelFit.recommendations.modelsLink", "Model management")}
				</Button>
				<Button
					leftSection={<IconRefresh size={16} />}
					loading={isRefreshing}
					disabled={!canRefresh}
					onClick={onRefresh}
					data-testid="model-fit-refresh-button"
				>
					{t("pages.modelFit.recommendations.refreshButton", "Refresh now")}
				</Button>
			</Group>
		</Group>
	);
}
