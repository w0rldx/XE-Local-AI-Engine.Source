import { Button, Card, Divider, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconDownload, IconPlugConnected, IconPlugConnectedX, IconSquare, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import { formatDurationSeconds } from "@/core/formatting/TimeFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkJudgePanel } from "@/features/benchmarks/components/BenchmarkJudgePanel";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { BenchmarkLaunchEvidencePanel } from "@/features/benchmarks/components/BenchmarkLaunchEvidencePanel";
import { BenchmarkReviewForm } from "@/features/benchmarks/components/BenchmarkReviewForm";
import { BenchmarkScorePicker } from "@/features/benchmarks/components/BenchmarkScorePicker";
import {
	BenchmarkIncompleteBadge,
	BenchmarkReasoningExhaustedBadge,
	BenchmarkStatusBadge,
	BenchmarkTruncatedBadge,
} from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type {
	BenchmarkJudgeMode,
	BenchmarkOutputPart,
	BenchmarkReviewSubmission,
	BenchmarkRubricCriterion,
	BenchmarkRunArtifact,
	BenchmarkRunDetail,
	BenchmarkScoreControl,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkToolCalls,
	formatBenchmarkToolSeconds,
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isPrimaryActive,
	isRunTerminal,
	toChatMessageParts,
} from "@/features/benchmarks/models/BenchmarkModels";
import { formatLatencyMs, formatTokensPerSecond, hasThroughputBreakdown } from "@/features/benchmarks/models/BenchmarkThroughput";
import { MessageParts } from "@/features/chat/components/MessageParts";

interface BenchmarkRunPaneProps {
	run: BenchmarkRunDetail;
	/** The project's judging mode; the judge panel has a reading only for `pointwise`. */
	judgeMode?: BenchmarkJudgeMode;
	parts: BenchmarkOutputPart[];
	isConnected: boolean;
	isReconnecting: boolean;
	isCancelling?: boolean;
	isScoring?: boolean;
	isJudgeBusy?: boolean;
	isDeleting?: boolean;
	onCancel: (target: "Primary" | "Judge") => void;
	onScore: (score: number) => void;
	onClearScore: () => void;
	onRejudge: () => void;
	onDelete: () => void;
	/**
	 * Set on a scoring run of a review project: the review form then REPLACES the score picker, because the node refuses
	 * a plain operator score there (409 `ReviewRequired`). `criteria` is null when the project has no judge policy.
	 */
	review?: {
		criteria: readonly BenchmarkRubricCriterion[] | null;
		isSaving: boolean;
		onSubmit: (review: BenchmarkReviewSubmission) => void;
	} | null;
	/**
	 * Which score control to offer; see `benchmarkScoreControl`. `judgeOnly` renders a note instead of any control,
	 * because in a review project the node refuses a plain score on every run and a review on the non-gated ones.
	 */
	scoreControl?: BenchmarkScoreControl;
	isDownloadingArtifact?: boolean;
	onDownloadArtifact?: (artifact: BenchmarkRunArtifact) => void;
}

// Per-call execution time, joined from the tool_result parts. The chat renderer has no slot for it, so the timing is a
// list of its own; a call the node did not time renders nothing rather than "0 s".
function ToolTiming({ run, parts }: { run: BenchmarkRunDetail; parts: readonly BenchmarkOutputPart[] }) {
	const { t } = useTranslation();
	const calls = benchmarkToolCalls(parts).filter((call) => call.durationMs !== null || call.repeated);
	if (run.toolCallCount === null && calls.length === 0) {
		return null;
	}
	return (
		<Stack gap={2} data-testid="benchmark-tool-timing">
			{run.toolCallCount === null ? null : (
				<Text size="sm" data-testid="benchmark-tool-rollup">
					{t("pages.benchmarks.tools.count", "{{count}} tool calls", { count: run.toolCallCount })}
					{run.toolTimeMs === null ? "" : ` · ${formatBenchmarkToolSeconds(run.toolTimeMs)}`}
				</Text>
			)}
			{calls.map((call) => (
				<Group key={call.key} gap="xs" data-testid={`benchmark-tool-${call.id}`}>
					<Text size="xs" ff="monospace">
						{call.name}
					</Text>
					{call.durationMs === null ? null : (
						<Text size="xs" c="dimmed" data-testid={`benchmark-tool-duration-${call.id}`}>
							{formatBenchmarkToolSeconds(call.durationMs)}
						</Text>
					)}
					{call.repeated ? (
						<StatusBadge
							color="yellow"
							label={t("pages.benchmarks.tools.repeated", "repeated call")}
							data-testid={`benchmark-tool-repeated-${call.id}`}
						/>
					) : null}
				</Group>
			))}
		</Stack>
	);
}

// Verifier evidence (the pythonTests streams). The bytes are the model's code output, so they are only ever downloaded.
function ArtifactList({
	run,
	isDownloading,
	onDownload,
}: {
	run: BenchmarkRunDetail;
	isDownloading: boolean;
	onDownload: (artifact: BenchmarkRunArtifact) => void;
}) {
	const { t } = useTranslation();
	if (run.artifacts.length === 0) {
		return null;
	}
	return (
		<Stack gap={4} data-testid="benchmark-run-artifacts">
			<Text size="sm" fw={600}>
				{t("pages.benchmarks.artifacts.title", "Verifier evidence files")}
			</Text>
			{run.artifacts.map((artifact) => (
				<Group key={artifact.id} gap="xs" justify="space-between" data-testid={`benchmark-artifact-${artifact.id}`}>
					<Text size="xs">
						{t(`pages.benchmarks.artifacts.kinds.${artifact.kind}`, artifact.kind)}
						{artifact.criterionId === null ? "" : ` · ${artifact.criterionId}`} · {humanizeBytes(artifact.byteCount)}
					</Text>
					<Button
						size="compact-xs"
						variant="subtle"
						leftSection={<IconDownload size={12} />}
						disabled={isDownloading}
						onClick={() => onDownload(artifact)}
						data-testid={`benchmark-artifact-download-${artifact.id}`}
					>
						{t("pages.benchmarks.artifacts.download", "Download")}
					</Button>
				</Group>
			))}
		</Stack>
	);
}

// The split behind the single tok/s figure above. Rendered only when the runtime actually reported it — a cloud run has
// no prefill/decode timings, and a row of dashes would read as a measurement of zero rather than as no measurement.
function ThroughputBreakdown({ run }: { run: BenchmarkRunDetail }) {
	const { t } = useTranslation();
	const {
		ttftMs,
		promptTokens,
		promptTokensPerSecond,
		generationTokens,
		generationTokensPerSecond,
		cachedPromptTokens,
		segmentCount,
	} = run.throughput;
	if (!hasThroughputBreakdown(run.throughput)) {
		return null;
	}
	const rows: { key: string; label: string; value: string }[] = [
		{ key: "ttft", label: t("pages.benchmarks.metrics.ttft", "Time to first token"), value: formatLatencyMs(ttftMs) },
		{
			key: "pp",
			label: t("pages.benchmarks.metrics.pp", "Prompt processing (pp)"),
			value: `${formatTokensPerSecond(promptTokensPerSecond)}${promptTokens === null ? "" : ` · ${promptTokens} tok`}`,
		},
		{
			key: "tg",
			label: t("pages.benchmarks.metrics.tg", "Generation (tg)"),
			value: `${formatTokensPerSecond(generationTokensPerSecond)}${generationTokens === null ? "" : ` · ${generationTokens} tok`}`,
		},
	];
	return (
		<Stack gap={2} data-testid="benchmark-throughput-breakdown">
			<Group gap="lg">
				{rows.map((row) => (
					<Text key={row.key} size="sm" data-testid={`benchmark-throughput-${row.key}`}>
						{row.label}: <b>{row.value}</b>
					</Text>
				))}
			</Group>
			{segmentCount !== null && segmentCount > 1 ? (
				<Text size="xs" c="dimmed" data-testid="benchmark-throughput-segments">
					{t(
						"pages.benchmarks.metrics.segments",
						"Summed over {{count}} model requests — the agent called tools, and every round prefilled again.",
						{
							count: segmentCount,
						},
					)}
				</Text>
			) : null}
			{cachedPromptTokens !== null && cachedPromptTokens > 0 ? (
				<Text size="xs" c="dimmed" data-testid="benchmark-throughput-cached">
					{t(
						"pages.benchmarks.metrics.cached",
						"{{tokens}} prompt tokens were reused from the KV cache — the prompt speed is not a cold prefill.",
						{ tokens: cachedPromptTokens },
					)}
				</Text>
			) : null}
		</Stack>
	);
}

export function BenchmarkRunPane({
	run,
	judgeMode,
	parts,
	isConnected,
	isReconnecting,
	isCancelling,
	isScoring,
	isJudgeBusy,
	isDeleting,
	onCancel,
	onScore,
	onClearScore,
	onRejudge,
	onDelete,
	review = null,
	scoreControl = "picker",
	isDownloadingArtifact = false,
	onDownloadArtifact,
}: BenchmarkRunPaneProps) {
	const { t } = useTranslation();
	const active = isPrimaryActive(run.primaryStatus);
	const truncated = isBenchmarkRunTruncated(run);
	const messageParts = toChatMessageParts(parts);
	return (
		<Card withBorder={true} radius="md" padding="lg" data-testid={`benchmark-run-${run.id}`}>
			<Stack gap="md">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Title order={3} size="h4">
							{run.primaryModelName}
						</Title>
						<Text size="xs" c="dimmed">
							{t(`pages.benchmarks.origin.${run.primaryModelOrigin ?? "legacy"}`, run.primaryModelOrigin ?? "Legacy / Unknown")}
						</Text>
					</Stack>
					<Group gap="xs">
						<BenchmarkStatusBadge status={run.primaryStatus} />
						{isBenchmarkRunReasoningExhausted(run) ? (
							<BenchmarkReasoningExhaustedBadge />
						) : truncated ? (
							<BenchmarkTruncatedBadge />
						) : null}
						{isBenchmarkRunIncomplete(run) ? <BenchmarkIncompleteBadge /> : null}
					</Group>
				</Group>
				<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid="benchmark-run-launch" />
				<Group gap="lg">
					<Text size="sm">
						{t("pages.benchmarks.metrics.duration", "Duration")}: <b>{formatDurationSeconds(run.durationMs)}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.metrics.tokens", "Tokens")}: <b>{run.totalTokens ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.metrics.speed", "tok/s")}: <b>{run.tokensPerSecond?.toFixed(1) ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.rank.quality", "Quality")}: <b>{run.qualityScore ?? "—"}</b>
					</Text>
				</Group>
				{/* What this run actually sampled with. In throughput mode it is the proof the repeats were deterministic;
				    in answer-variance mode the seed is the only thing that says which of the repeats this one is. */}
				<Group gap="lg" data-testid="benchmark-run-sampling">
					<Text size="sm">
						{t("pages.benchmarks.run.repeatMode", "Repeat mode")}:{" "}
						<b>{t(`pages.benchmarks.run.repeatModes.${run.repeatMode}`, run.repeatMode)}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.run.samplingTemperature", "Temperature")}: <b>{run.samplingTemperature ?? "—"}</b>
					</Text>
					<Text size="sm">
						{t("pages.benchmarks.run.samplingSeed", "Seed")}: <b>{run.samplingSeed ?? "—"}</b>
					</Text>
					{run.reasoningBudgetTokens === null ? null : (
						<Text size="sm">
							{t("pages.benchmarks.project.reasoningBudget", "Reasoning budget (tokens)")}: <b>{run.reasoningBudgetTokens}</b>
							{/* A budget the runtime never applied is why the run can look as if it did nothing. Saying so
							    beats letting the operator raise a number that was never read. */}
							{run.reasoningBudgetApplicable === false ? (
								<Text span={true} c="dimmed" data-testid="benchmark-run-budget-not-applicable">
									{" "}
									{t("pages.benchmarks.run.reasoningBudgetNotApplicable", "not applied — model has no thinking mode")}
								</Text>
							) : null}
						</Text>
					)}
				</Group>
				<ThroughputBreakdown run={run} />
				<ToolTiming run={run} parts={parts} />
				<Group gap="xs" c={isConnected ? "green" : "dimmed"}>
					{isConnected ? <IconPlugConnected size={16} /> : <IconPlugConnectedX size={16} />}
					<Text size="xs">
						{isReconnecting
							? t("pages.benchmarks.connection.reconnecting", "Reconnecting and reconciling…")
							: isConnected
								? t("pages.benchmarks.connection.live", "Live updates connected")
								: t("pages.benchmarks.connection.http", "Using durable HTTP state")}
					</Text>
				</Group>
				<Divider />
				{active && messageParts.length === 0 ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.benchmarks.run.waiting", "Waiting for model output…")}</Text>
					</Group>
				) : null}
				<MessageParts parts={messageParts} isStreaming={run.primaryStatus === "Running"} />
				{run.primaryStatus === "Failed" && run.primaryErrorMessage ? (
					<InlineErrorAlert message={run.primaryErrorMessage} />
				) : null}
				{active ? (
					<Button
						variant="light"
						color="red"
						leftSection={<IconSquare size={14} />}
						loading={isCancelling}
						onClick={() => onCancel("Primary")}
					>
						{t("pages.benchmarks.run.cancel", "Cancel run")}
					</Button>
				) : null}
				{review ? (
					<BenchmarkReviewForm
						// Re-seeded whenever the judging or the stored decision moves, so the pre-fill never shows stale scores. The
						// judge state is in the key because an attempt that succeeds while the pane is open keeps its sequence; the
						// remount discards nothing, since the form is locked until the judging succeeded.
						key={`${run.id}-${run.judge.attemptSequence ?? 0}-${run.judge.state}-${run.review?.updatedAtUtc ?? 0}`}
						run={run}
						criteria={review.criteria}
						isSaving={review.isSaving}
						onSubmit={review.onSubmit}
					/>
				) : scoreControl === "judgeOnly" ? (
					<Text size="sm" c="dimmed" data-testid="benchmark-score-judge-only">
						{t("pages.benchmarks.review.judgeOnly", "Scored by its judge; this run is not reviewable.")}
					</Text>
				) : scoreControl === "picker" ? (
					<BenchmarkScorePicker
						value={run.userScore}
						disabled={run.primaryStatus !== "Succeeded"}
						isSaving={isScoring}
						onChange={onScore}
						onClear={onClearScore}
					/>
				) : null}
				<BenchmarkJudgePanel
					judge={run.judge}
					mode={judgeMode}
					primaryTruncated={truncated}
					// The DECODED token count, not run.totalTokens — that one is prompt + output + reasoning, so a long
					// question would inflate the answer length the judge is counterweighted against. Null on a run whose
					// runtime reported no timings, which renders nothing rather than an overstated number.
					outputTokens={run.throughput.generationTokens}
					canRejudge={run.primaryStatus === "Succeeded"}
					isBusy={isJudgeBusy}
					onCancel={() => onCancel("Judge")}
					onRejudge={onRejudge}
				/>
				{onDownloadArtifact ? (
					<ArtifactList run={run} isDownloading={isDownloadingArtifact} onDownload={onDownloadArtifact} />
				) : null}
				<BenchmarkLaunchEvidencePanel run={run} />
				{isRunTerminal(run) ? (
					<Button variant="subtle" color="red" leftSection={<IconTrash size={14} />} loading={isDeleting} onClick={onDelete}>
						{t("pages.benchmarks.run.delete", "Delete terminal run")}
					</Button>
				) : null}
			</Stack>
		</Card>
	);
}
