import { Button, Group, Stack, Text, Title } from "@mantine/core";
import { IconCalendarClock, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface SchedulerPageHeaderProps {
	onCreate: () => void;
}

// Page header for the scheduler: eyebrow, title, subtitle, and the Create-job action. Split out of SchedulerPage
// to keep the page component small.
export function SchedulerPageHeader({ onCreate }: SchedulerPageHeaderProps) {
	const { t } = useTranslation();

	return (
		<Group justify="space-between" align="flex-start">
			<Stack gap={4}>
				<Text size="sm" tt="uppercase" fw={700} c="dimmed">
					{t("pages.scheduler.eyebrow", "Worker Node")}
				</Text>
				<Group gap="xs" align="center">
					<IconCalendarClock size={24} />
					<Title order={2}>{t("pages.scheduler.title", "Scheduler")}</Title>
				</Group>
				<Text c="dimmed">
					{t(
						"pages.scheduler.subtitle",
						"Schedule recurring and one-off jobs on this node. Jobs are disabled until you enable them, and parameters are stored encrypted.",
					)}
				</Text>
			</Stack>
			<Button leftSection={<IconPlus size={16} />} onClick={onCreate} data-testid="scheduler-create-button">
				{t("pages.scheduler.createButton", "Create job")}
			</Button>
		</Group>
	);
}
