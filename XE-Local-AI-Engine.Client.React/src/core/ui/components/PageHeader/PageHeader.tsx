import { Box, Group, Stack, Text, Title } from "@mantine/core";
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
			<Stack gap={4} style={{ flex: "1 1 auto", minWidth: 0 }}>
				<Text size="sm" tt="uppercase" fw={700} c="dimmed">
					{eyebrow ?? t("common.workerNode", "Worker Node")}
				</Text>
				{/*
				 * `wrap="nowrap"` keeps the icon on the title's line: with the default wrap a title too long for the
				 * remaining width pushed the icon onto a line of its own ABOVE the heading (live-observed on
				 * /benchmarks at 390px). The icon is held at its intrinsic size and the title is the part allowed to
				 * shrink, so a long title wraps or truncates INSIDE the row instead of breaking it apart.
				 */}
				<Group gap="xs" align="center" wrap="nowrap" data-testid="page-header-title-row">
					{icon ? <Box style={{ display: "flex", flex: "0 0 auto" }}>{icon}</Box> : null}
					<Title order={2} style={{ minWidth: 0 }}>
						{title}
					</Title>
				</Group>
				{subtitle ? <Text c="dimmed">{subtitle}</Text> : null}
			</Stack>
			{actions ? <Group gap="sm">{actions}</Group> : null}
		</Group>
	);
}
