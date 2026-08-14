import { Group, Stack, Text, Title } from "@mantine/core";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

interface PageHeaderProps {
	title: ReactNode;
	/** Leading icon next to the title; by convention a Tabler icon with size={24}. */
	icon?: ReactNode;
	subtitle?: ReactNode;
	/** Uppercase kicker above the title. Defaults to the shared "Worker Node" label. */
	eyebrow?: ReactNode;
	/** Right-aligned header actions; multiple children are spaced with gap="sm". */
	actions?: ReactNode;
	"data-tour"?: string;
	"data-testid"?: string;
}

// Standard page header: eyebrow, icon + h2 title, dimmed subtitle, and a right-aligned action slot.
// All routed pages use this so the header buildup reads the same everywhere.
export function PageHeader({
	title,
	icon,
	subtitle,
	eyebrow,
	actions,
	"data-tour": dataTour,
	"data-testid": testId,
}: PageHeaderProps) {
	const { t } = useTranslation();

	return (
		<Group justify="space-between" align="flex-start" data-tour={dataTour} data-testid={testId}>
			<Stack gap={4}>
				<Text size="sm" tt="uppercase" fw={700} c="dimmed">
					{eyebrow ?? t("common.workerNode", "Worker Node")}
				</Text>
				<Group gap="xs" align="center">
					{icon}
					<Title order={2}>{title}</Title>
				</Group>
				{subtitle ? <Text c="dimmed">{subtitle}</Text> : null}
			</Stack>
			{actions ? <Group gap="sm">{actions}</Group> : null}
		</Group>
	);
}
