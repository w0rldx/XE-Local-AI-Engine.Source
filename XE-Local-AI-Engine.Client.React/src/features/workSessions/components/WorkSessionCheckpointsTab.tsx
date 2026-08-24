import { Alert, Anchor, Collapse, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import type { WorkSessionCheckpointResponse } from "@/features/workSessions/models/WorkSessionModels";

export function WorkSessionCheckpointsTab({ checkpoints }: { checkpoints: readonly WorkSessionCheckpointResponse[] }) {
	const { t } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);

	if (checkpoints.length === 0) {
		return (
			<Alert color="gray" variant="light" data-testid="work-session-checkpoints-empty">
				{t("pages.workSessions.checkpoints.empty", "No checkpoint has been written yet.")}
			</Alert>
		);
	}

	// Newest first: the latest checkpoint is the recovery point a paused or interrupted session resumes from.
	const ordered = checkpoints.toSorted((left, right) => (right.step ?? 0) - (left.step ?? 0));

	return (
		<Stack gap="sm" data-testid="work-session-checkpoints-tab">
			{ordered.map((checkpoint) => (
				<Paper key={checkpoint.id} withBorder={true} p="xs" data-testid={`work-session-checkpoint-${checkpoint.id}`}>
					<Stack gap={4}>
						<Text size="xs" fw={700}>
							{t("pages.workSessions.checkpoints.step", "Checkpoint at step {{step}}", { step: checkpoint.step ?? 0 })}
						</Text>
						<Text size="xs" c="dimmed">
							{new Date(checkpoint.createdAtUtc ?? 0).toLocaleString()}
						</Text>
						<Text size="sm">
							{/* A NoLocalModel node cannot summarize, so P3 types Summary as nullable — say so rather than render "". */}
							{checkpoint.summary ?? t("pages.workSessions.checkpoints.noSummary", "No prose summary was produced.")}
						</Text>
						<Anchor
							component="button"
							type="button"
							size="xs"
							onClick={() => setExpandedId((current) => (current === checkpoint.id ? undefined : checkpoint.id))}
							data-testid={`work-session-checkpoint-toggle-${checkpoint.id}`}
						>
							{expandedId === checkpoint.id
								? t("pages.workSessions.checkpoints.hideState", "Hide state")
								: t("pages.workSessions.checkpoints.showState", "Show state")}
						</Anchor>
						<Collapse expanded={expandedId === checkpoint.id}>
							<CodeEditor
								value={checkpoint.stateJson ?? ""}
								language="json"
								readOnly={true}
								height={220}
								wordWrap={true}
								aria-label={t("pages.workSessions.checkpoints.stateLabel", "Checkpoint state")}
								data-testid={`work-session-checkpoint-state-${checkpoint.id}`}
							/>
						</Collapse>
					</Stack>
				</Paper>
			))}
		</Stack>
	);
}
