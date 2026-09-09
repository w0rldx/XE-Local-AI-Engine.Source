import { ActionIcon, Badge, Group, Paper, Stack, Text } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { GoldenConversation } from "@/features/agents/models/GoldenConversationModels";

interface GoldenConversationRowProps {
	goldenCase: GoldenConversation;
	disabled: boolean;
	onDelete: () => void;
}

// One golden case: title, the input-turn count, and presence badges for the scoring signals (assertion / rubric).
export function GoldenConversationRow({ goldenCase, disabled, onDelete }: GoldenConversationRowProps) {
	const { t } = useTranslation();

	return (
		<Paper withBorder={true} p="xs" key={goldenCase.id} data-testid={`golden-case-${goldenCase.id}`}>
			<Group justify="space-between" align="flex-start" wrap="nowrap">
				<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
					<Group gap="xs" align="center" wrap="wrap">
						<Text size="sm" fw={600}>
							{goldenCase.title}
						</Text>
						{!goldenCase.enabled ? (
							<Badge size="xs" variant="outline" color="gray" data-testid={`golden-case-disabled-${goldenCase.id}`}>
								{t("pages.agents.golden.disabledBadge", "disabled")}
							</Badge>
						) : null}
						{goldenCase.source === "harvested" ? (
							<Badge size="xs" variant="light" color="teal" data-testid={`golden-case-harvested-${goldenCase.id}`}>
								{t("pages.agents.golden.harvestedBadge", "harvested")}
							</Badge>
						) : null}
					</Group>
					<Group gap="xs" align="center" wrap="wrap">
						<Text size="xs" c="dimmed" data-testid={`golden-case-turns-${goldenCase.id}`}>
							{t("pages.agents.golden.turnCount", "{{count}} input turns", { count: goldenCase.inputTurns.length })}
						</Text>
						{goldenCase.assertion ? (
							<Badge size="xs" variant="light" color="blue" data-testid={`golden-case-assertion-${goldenCase.id}`}>
								{t("pages.agents.golden.hasAssertion", "assertion")}
							</Badge>
						) : null}
						{goldenCase.rubric ? (
							<Badge size="xs" variant="light" color="grape" data-testid={`golden-case-rubric-${goldenCase.id}`}>
								{t("pages.agents.golden.hasRubric", "rubric")}
							</Badge>
						) : null}
					</Group>
				</Stack>
				<ActionIcon
					aria-label={t("pages.agents.golden.deleteAria", "Delete golden case")}
					variant="subtle"
					color="red"
					size="sm"
					disabled={disabled}
					onClick={onDelete}
					data-testid={`golden-delete-${goldenCase.id}`}
				>
					<IconTrash size={14} />
				</ActionIcon>
			</Group>
		</Paper>
	);
}
