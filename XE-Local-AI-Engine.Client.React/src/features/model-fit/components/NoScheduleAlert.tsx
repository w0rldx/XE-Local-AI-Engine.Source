import { Alert, Anchor, Group, Text } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface NoScheduleAlertProps {
	readonly onOpenScheduler: () => void;
}

// Shown when no model-recommendation-check schedule exists yet: Refresh-now cannot fire without one, so this guides
// the operator to create the schedule. The parent decides when to render it (no job + jobs done loading).
export function NoScheduleAlert({ onOpenScheduler }: NoScheduleAlertProps) {
	const { t } = useTranslation();

	return (
		<Alert color="blue" icon={<IconInfoCircle size={16} />} data-testid="model-fit-no-job-guidance">
			<Group justify="space-between" align="center">
				<Text size="sm">
					{t(
						"pages.modelFit.recommendations.noJobGuidance",
						"No model-recommendation-check schedule exists yet. Create one in the Scheduler to enable refreshing.",
					)}
				</Text>
				<Anchor component="button" type="button" onClick={onOpenScheduler} data-testid="model-fit-no-job-scheduler-link">
					{t("pages.modelFit.recommendations.openScheduler", "Open Scheduler")}
				</Anchor>
			</Group>
		</Alert>
	);
}
