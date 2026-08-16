import { Alert, Button, Card, Group, Progress, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconRefresh, IconScale, IconSquare } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkJudgeStateBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkRunJudge } from "@/features/benchmarks/models/BenchmarkModels";
import { isJudgeActive } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkJudgePanelProps {
	judge: BenchmarkRunJudge;
	/** Only a succeeded primary has stored output to judge, so only then can a re-judge be offered. */
	canRejudge: boolean;
	isBusy?: boolean;
	onCancel: () => void;
	onRejudge: () => void;
}

const maxCriterionScore = 10;

/**
 * The current judging of one run: its state, the weighted 0..100 score, which policy revision produced it, the
 * per-criterion breakdown, and whether that score still counts towards the ranking. The two currency flags are shown
 * as their own chips rather than folded into the score — a stale score is still a real score, it just cannot be ranked
 * against runs judged under the current policy and judge runtime.
 */
export function BenchmarkJudgePanel({ judge, canRejudge, isBusy = false, onCancel, onRejudge }: BenchmarkJudgePanelProps) {
	const { t } = useTranslation();
	const active = isJudgeActive(judge.state);
	const explanation = t(`pages.benchmarks.judge.explanations.${judge.state}`, "");

	return (
		<Card withBorder={true} radius="md" padding="md" data-testid="benchmark-judge-panel">
			<Stack gap="sm">
				<Group justify="space-between">
					<Group gap="xs">
						<IconScale size={18} />
						<Title order={5}>{t("pages.benchmarks.judge.title", "Automated judge")}</Title>
					</Group>
					<BenchmarkJudgeStateBadge state={judge.state} />
				</Group>
				{explanation ? (
					<Text size="sm" c="dimmed">
						{explanation}
					</Text>
				) : null}
				<Group gap="xs">
					{judge.policyRevision === null ? null : (
						<StatusBadge
							color={judge.policyCurrent ? "blue" : "orange"}
							label={
								judge.policyCurrent
									? t("pages.benchmarks.judge.policyCurrent", "policy r{{revision}}", { revision: judge.policyRevision })
									: t("pages.benchmarks.judge.policyOutdated", "policy r{{revision}} — outdated", {
											revision: judge.policyRevision,
										})
							}
							data-testid="benchmark-judge-policy"
						/>
					)}
					{judge.state === "succeeded" && !judge.executionCurrent ? (
						<Tooltip label={t("pages.benchmarks.judge.runtimeDiffersHint", "Re-judge the project to move the cohort.")}>
							<span>
								<StatusBadge
									color="orange"
									label={t("pages.benchmarks.judge.runtimeDiffers", "judge runtime differs")}
									data-testid="benchmark-judge-runtime"
								/>
							</span>
						</Tooltip>
					) : null}
					{judge.attemptSequence === null ? null : (
						<Text size="xs" c="dimmed">
							{t("pages.benchmarks.judge.attempt", "attempt #{{sequence}}", { sequence: judge.attemptSequence })}
						</Text>
					)}
				</Group>
				{judge.score === null ? null : (
					<Text fw={700} data-testid="benchmark-judge-score">
						{t("pages.benchmarks.judge.score", "Judge score: {{score}} / 100", { score: judge.score })}
					</Text>
				)}
				{judge.summary ? <Text size="sm">{judge.summary}</Text> : null}
				{judge.criteria.length > 0 ? (
					<Stack gap="xs" data-testid="benchmark-judge-criteria">
						{judge.criteria.map((criterion) => (
							<Stack key={criterion.id} gap={2}>
								<Group justify="space-between" gap="xs">
									<Text size="sm" fw={600}>
										{criterion.id}
									</Text>
									<Text size="sm">
										{t("pages.benchmarks.judge.criterionScore", "{{score}} / 10", { score: criterion.score })}
									</Text>
								</Group>
								<Progress
									value={(Math.min(criterion.score, maxCriterionScore) / maxCriterionScore) * 100}
									aria-label={criterion.id}
									size="sm"
								/>
								<Text size="xs" c="dimmed">
									{criterion.rationale}
								</Text>
							</Stack>
						))}
					</Stack>
				) : null}
				{judge.state === "failed" && judge.errorMessage ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{judge.errorMessage}
					</Alert>
				) : null}
				<Group gap="xs">
					{active ? (
						<Button
							variant="subtle"
							color="red"
							size="xs"
							leftSection={<IconSquare size={14} />}
							loading={isBusy}
							onClick={onCancel}
						>
							{t("pages.benchmarks.judge.cancel", "Cancel judge")}
						</Button>
					) : null}
					{!active && canRejudge ? (
						<Button variant="light" size="xs" leftSection={<IconRefresh size={14} />} loading={isBusy} onClick={onRejudge}>
							{t("pages.benchmarks.judge.rejudge", "Re-judge run")}
						</Button>
					) : null}
				</Group>
			</Stack>
		</Card>
	);
}
