import { Button, Group, Paper, Stack, Text } from "@mantine/core";
import { IconCheck, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { truncate } from "@/features/agents/models/GoldenConversationFormHelpers";
import type { GoldenConversation } from "@/features/agents/models/GoldenConversationModels";

// First-turn preview of a harvested candidate's input conversation, used in the pending-review sub-section so the
// operator can judge the case without expanding it: the count plus a truncated first turn.
const PENDING_TURN_PREVIEW_MAX = 120;
const PENDING_RUBRIC_PREVIEW_MAX = 200;

interface GoldenPendingRowProps {
	goldenCase: GoldenConversation;
	disabled: boolean;
	onApprove: () => void;
	onReject: () => void;
}

// One harvested candidate in the pending-review sub-section: title, a turns preview (count + first turn), the seeded
// rubric (truncated), and Approve/Reject. Approve flips it into the active golden set; Reject deletes it (same
// ownership-guarded delete as a manual case, with the existing confirm pattern).
export function GoldenPendingRow({ goldenCase, disabled, onApprove, onReject }: GoldenPendingRowProps) {
	const { t } = useTranslation();

	const firstTurn = goldenCase.inputTurns[0];
	const rubricPreview = goldenCase.rubric ? truncate(goldenCase.rubric, PENDING_RUBRIC_PREVIEW_MAX) : null;

	return (
		<Paper withBorder={true} p="xs" data-testid={`golden-pending-${goldenCase.id}`}>
			<Stack gap={6}>
				<Text size="sm" fw={600}>
					{goldenCase.title}
				</Text>
				<Text size="xs" c="dimmed" data-testid={`golden-pending-turns-${goldenCase.id}`}>
					{firstTurn
						? t("pages.agents.golden.pending.turnsPreview", "{{count}} turns · {{role}}: {{text}}", {
								count: goldenCase.inputTurns.length,
								role: firstTurn.role,
								text: truncate(firstTurn.text, PENDING_TURN_PREVIEW_MAX),
							})
						: t("pages.agents.golden.pending.turnCount", "{{count}} input turns", {
								count: goldenCase.inputTurns.length,
							})}
				</Text>
				{rubricPreview ? (
					<Text size="xs" c="dimmed" data-testid={`golden-pending-rubric-${goldenCase.id}`}>
						{t("pages.agents.golden.pending.rubric", "Rubric: {{rubric}}", { rubric: rubricPreview })}
					</Text>
				) : null}
				<Group justify="flex-end" gap="xs">
					<Button
						size="xs"
						variant="subtle"
						color="red"
						leftSection={<IconTrash size={14} />}
						onClick={onReject}
						disabled={disabled}
						data-testid={`golden-pending-reject-${goldenCase.id}`}
					>
						{t("pages.agents.golden.pending.reject", "Reject")}
					</Button>
					<Button
						size="xs"
						variant="light"
						color="teal"
						leftSection={<IconCheck size={14} />}
						onClick={onApprove}
						disabled={disabled}
						data-testid={`golden-pending-approve-${goldenCase.id}`}
					>
						{t("pages.agents.golden.pending.approve", "Approve")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}
