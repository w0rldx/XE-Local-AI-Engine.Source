import { Alert, Button, Card, Group, Progress, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconRefresh, IconScale, IconSquare, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkJudgeStateBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkJudgeMode, BenchmarkRunJudge } from "@/features/benchmarks/models/BenchmarkModels";
import { isJudgeActive } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkJudgePanelProps {
	judge: BenchmarkRunJudge;
	/** The project's judging mode. Only `pointwise` has a reading this panel can render. */
	mode?: BenchmarkJudgeMode;
	/** The primary answer was cut off by the token budget, so this verdict graded a fragment. */
	primaryTruncated?: boolean;
	/**
	 * Tokens the graded answer ran to, shown beside the score. Rewarding a longer answer for being longer is an LLM
	 * judge's best-documented bias, so the length sits next to the number it may have inflated: "88 / 100 · 4200 tokens"
	 * beside "85 / 100 · 300 tokens" is a comparison the reader can make without opening either transcript.
	 */
	outputTokens?: number | null;
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
 *
 * POINTWISE ONLY, and deliberately so. This panel reads one score with one rationale per criterion, which is exactly
 * what the pointwise rubric judge produces. Pairwise is a different reading: no per-run rubric score at all, but a
 * verdict matrix over run pairs and a Bradley-Terry fit whose per-run number carries a bootstrap CI, plus a
 * `pairwise-pending` / `pairwise-insufficient` exclusion vocabulary the ranking helper does not know.
 *
 * The caller passes `mode` and gets nothing back for `pairwise` — that arm renders a `BenchmarkPairwiseMatrix` when
 * the backend slice that executes it lands (plan §7.4-§7.5). Refusing to render is the honest answer meanwhile:
 * showing pointwise chrome for a pairwise policy would present a score shape the policy does not produce. The node
 * refuses to SAVE a pairwise policy today (422 `PairwiseNotAvailable`), so no stored run reaches this arm.
 */
export function BenchmarkJudgePanel({
	judge,
	mode = "pointwise",
	canRejudge,
	primaryTruncated = false,
	outputTokens = null,
	isBusy = false,
	onCancel,
	onRejudge,
}: BenchmarkJudgePanelProps) {
	const { t } = useTranslation();
	const active = isJudgeActive(judge.state);
	const explanation = t(`pages.benchmarks.judge.explanations.${judge.state}`, "");
	// follow-up: render <BenchmarkPairwiseMatrix /> here once the pairwise executor ships (plan §7.5).
	const verifiers = new Map(judge.verifiers.map((verifier) => [verifier.id, verifier]));

	if (mode !== "pointwise") {
		return null;
	}

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
				{primaryTruncated ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-judge-truncated-notice">
					{t(
						"pages.benchmarks.judge.truncatedPrimary",
						"The answer this verdict graded was cut off by the token budget, so the score describes an incomplete answer and does not rank.",
					)}
				</Alert>
			) : null}
			{judge.score === null ? null : (
					<Group gap="xs" align="baseline">
						<Text fw={700} data-testid="benchmark-judge-score">
							{t("pages.benchmarks.judge.score", "Judge score: {{score}} / 100", { score: judge.score })}
						</Text>
						{outputTokens === null ? null : (
							<Text size="sm" c="dimmed" data-testid="benchmark-judge-output-length">
								{t("pages.benchmarks.judge.outputLength", "· {{tokens}} output tokens", { tokens: outputTokens })}
							</Text>
						)}
					</Group>
				)}
				{judge.summary ? <Text size="sm">{judge.summary}</Text> : null}
				{judge.criteria.length > 0 ? (
					<Stack gap="xs" data-testid="benchmark-judge-criteria">
						{judge.criteria.map((criterion) => {
							// A verifiable criterion's score was DECIDED, not judged: the badge says which side produced it,
							// so a 10 that came from a regex match is not read as a model's opinion that happened to agree.
							const verifier = verifiers.get(criterion.id);
							return (
								<Stack key={criterion.id} gap={2}>
									<Group justify="space-between" gap="xs">
										<Group gap={6} wrap="nowrap">
											<Text size="sm" fw={600}>
												{criterion.id}
											</Text>
											{verifier === undefined ? null : (
												<StatusBadge
													color={verifier.passed ? "green" : "red"}
													label={t(`pages.benchmarks.verifier.kinds.${verifier.kind}`, verifier.kind)}
													data-testid={`benchmark-judge-verifier-${criterion.id}`}
												/>
											)}
										</Group>
										<Text size="sm">
											{t("pages.benchmarks.judge.criterionScore", "{{score}} / 10", { score: criterion.score })}
										</Text>
									</Group>
									<Progress
										value={(Math.min(criterion.score, maxCriterionScore) / maxCriterionScore) * 100}
										aria-label={criterion.id}
										size="sm"
									/>
									{verifier === undefined ? null : (
										<Group gap={4} wrap="nowrap" align="center">
											{verifier.passed ? (
												<IconCheck size={14} color="var(--mantine-color-green-6)" />
											) : (
												<IconX size={14} color="var(--mantine-color-red-6)" />
											)}
											<Text size="xs" c="dimmed" data-testid={`benchmark-judge-verifier-detail-${criterion.id}`}>
												{verifier.detail}
											</Text>
										</Group>
									)}
									<Text size="xs" c="dimmed">
										{criterion.rationale}
									</Text>
								</Stack>
							);
						})}
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
