import { Alert, Code, Group, Paper, Stack, Switch, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";
import {
	toWorkSessionFindingKind,
	type WorkSessionFindingKind,
	type WorkSessionFindingResponse,
	workSessionFindingKinds,
} from "@/features/workSessions/models/WorkSessionModels";

export function WorkSessionFindingsTab({ findings }: { findings: readonly WorkSessionFindingResponse[] }) {
	const { t } = useTranslation();
	const [showSuperseded, setShowSuperseded] = useState(false);

	const visible = findings.filter((finding) => showSuperseded || finding.superseded !== true);
	const grouped = workSessionFindingKinds
		.map((kind) => ({ kind, items: visible.filter((finding) => toWorkSessionFindingKind(finding.kind) === kind) }))
		.filter((group) => group.items.length > 0);

	return (
		<Stack gap="sm" data-testid="work-session-findings-tab">
			<Group justify="space-between" wrap="nowrap">
				<Text size="xs" c="dimmed">
					{t("pages.workSessions.findings.count", "{{count}} findings", { count: visible.length })}
				</Text>
				<Switch
					size="xs"
					checked={showSuperseded}
					onChange={(event) => setShowSuperseded(event.currentTarget.checked)}
					label={t("pages.workSessions.findings.showSuperseded", "Show superseded")}
					data-testid="work-session-findings-show-superseded"
				/>
			</Group>
			{grouped.length === 0 ? (
				<Alert color="gray" variant="light" data-testid="work-session-findings-empty">
					{t("pages.workSessions.findings.empty", "The agent has not recorded anything yet.")}
				</Alert>
			) : (
				grouped.map((group) => (
					<Stack key={group.kind} gap="xs" data-testid={`work-session-findings-group-${group.kind}`}>
						<Text size="xs" fw={700} tt="uppercase" c="dimmed">
							{findingKindLabel(group.kind, t)}
						</Text>
						{group.items.map((finding) => (
							<Paper
								key={finding.id}
								withBorder={true}
								p="xs"
								opacity={finding.superseded === true ? 0.55 : 1}
								data-testid={`work-session-finding-${finding.id}`}
								data-superseded={finding.superseded === true ? "true" : undefined}
							>
								<Stack gap={4}>
									<MarkdownView content={finding.text ?? ""} />
									{finding.sourceRef ? (
										<Code data-testid={`work-session-finding-source-${finding.id}`}>{finding.sourceRef}</Code>
									) : null}
									<Text size="xs" c="dimmed">
										{t("pages.workSessions.findings.step", "Step {{step}}", { step: finding.createdStep ?? 0 })}
									</Text>
								</Stack>
							</Paper>
						))}
					</Stack>
				))
			)}
		</Stack>
	);
}

function findingKindLabel(kind: WorkSessionFindingKind, t: (key: string, fallback: string) => string): string {
	return t(`pages.workSessions.findingKind.${kind}`, kind);
}
