import { Button, Group, Loader, Progress, Stack, Text } from "@mantine/core";
import { IconActivity, IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useActiveOperations } from "@/core/operations/hooks/useActiveOperations";
import type { ActiveOperation } from "@/core/operations/models/OperationModels";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

function ActivityRow({ operation }: { operation: ActiveOperation }) {
	const { t } = useTranslation();
	const failed = operation.state === "failed";

	return (
		<Stack gap={4} data-testid="home-activity-item" data-state={operation.state}>
			<Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
				<Group gap="xs" align="center" wrap="nowrap" style={{ minWidth: 0 }}>
					{failed ? (
						<IconAlertTriangle size={16} color="var(--mantine-color-red-6)" />
					) : operation.progress.kind === "indeterminate" ? (
						<Loader size="xs" />
					) : null}
					<Text size="sm" fw={500} c={failed ? "red" : undefined}>
						{operation.label}
					</Text>
				</Group>
				<Group gap="xs" wrap="nowrap">
					{operation.retry ? (
						<Button size="compact-sm" color="red" onClick={operation.retry} data-testid="home-activity-retry">
							{t("pages.home.activity.retry", "Try again")}
						</Button>
					) : null}
					<ButtonLink {...operation.href} size="compact-sm" variant="subtle" data-testid="home-activity-open">
						{t("pages.home.activity.open", "Open")}
					</ButtonLink>
				</Group>
			</Group>
			{operation.detail ? (
				<Text size="xs" c={failed ? "red" : "dimmed"} data-testid="home-activity-detail">
					{operation.detail}
				</Text>
			) : null}
			{operation.progress.kind === "known" ? (
				<Progress value={operation.progress.pct} size="sm" radius="sm" aria-label={operation.label} />
			) : null}
		</Stack>
	);
}

// What the node is doing right now: running and failed operations from the app-wide feed. With nothing to show the
// card is not rendered at all, so an idle node's Home carries no empty box.
export function HomeActivityCard() {
	const { t } = useTranslation();
	const operations = useActiveOperations();

	if (operations.length === 0) {
		return null;
	}

	return (
		<SectionCard
			title={t("pages.home.activity.title", "Activity")}
			icon={<IconActivity size={22} />}
			gap="sm"
			data-testid="home-activity"
		>
			{operations.map((operation) => (
				<ActivityRow key={operation.id} operation={operation} />
			))}
		</SectionCard>
	);
}
