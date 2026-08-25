import type { MantineColor } from "@mantine/core";
import { Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkJudgeState, BenchmarkPrimaryStatus } from "@/features/benchmarks/models/BenchmarkModels";
import { isJudgeActive, isPrimaryActive } from "@/features/benchmarks/models/BenchmarkModels";

const primaryColors: Record<BenchmarkPrimaryStatus, MantineColor> = {
	Queued: "yellow",
	Running: "blue",
	CancelRequested: "orange",
	Succeeded: "green",
	Failed: "red",
	Cancelled: "gray",
};

// The judging's own lifecycle, which is independent from the primary one: a failed judge is not a failed run, and
// "none" means this run carries no judging at all rather than a judging that produced nothing.
const judgeColors: Record<BenchmarkJudgeState, MantineColor> = {
	none: "gray",
	queued: "yellow",
	running: "blue",
	succeeded: "green",
	failed: "red",
	cancelled: "gray",
};

export function BenchmarkStatusBadge({ status }: { status: BenchmarkPrimaryStatus }) {
	const { t } = useTranslation();
	const label = t(`pages.benchmarks.status.${status}`, status);
	return <StatusBadge color={primaryColors[status]} label={label} inProgress={isPrimaryActive(status)} aria-label={label} />;
}

/**
 * Shown next to a Succeeded status, never instead of it: the run really did succeed, and the badge is what stops the
 * reader from taking a fragment — or an empty answer — for a finished one. Three readings of one stop reason share it,
 * because the difference between them is only which sentence tells the operator what to do next.
 */
function StopReasonBadge({ color, label, hint, testId }: { color: MantineColor; label: string; hint: string; testId: string }) {
	return (
		<Tooltip label={hint} multiline={true} w={260}>
			<span>
				<StatusBadge color={color} label={label} aria-label={label} data-testid={testId} />
			</span>
		</Tooltip>
	);
}

export function BenchmarkTruncatedBadge({ testId }: { testId?: string }) {
	const { t } = useTranslation();
	return (
		<StopReasonBadge
			color="orange"
			label={t("pages.benchmarks.status.truncated", "Truncated")}
			hint={t(
				"pages.benchmarks.status.truncatedHint",
				"The answer was cut off by the token budget. It does not rank — rerun with a larger context or output budget.",
			)}
			testId={testId ?? "benchmark-truncated"}
		/>
	);
}

/** Truncated, but inside the thinking: the reasoning budget is the one to raise, not the output budget. */
export function BenchmarkReasoningExhaustedBadge({ testId }: { testId?: string }) {
	const { t } = useTranslation();
	return (
		<StopReasonBadge
			color="grape"
			label={t("pages.benchmarks.status.reasoningExhausted", "Reasoning budget spent")}
			hint={t(
				"pages.benchmarks.status.reasoningExhaustedHint",
				"The run spent its whole reasoning budget without answering. It does not rank — raise the project's reasoning budget, not the output budget.",
			)}
			testId={testId ?? "benchmark-reasoning-exhausted"}
		/>
	);
}

/** No budget ran out: the run stopped cleanly and produced no answer, so raising a budget changes nothing. */
export function BenchmarkIncompleteBadge({ testId }: { testId?: string }) {
	const { t } = useTranslation();
	return (
		<StopReasonBadge
			color="red"
			label={t("pages.benchmarks.status.incomplete", "No answer")}
			hint={t(
				"pages.benchmarks.status.incompleteHint",
				"The run finished without answering — it stopped on an unanswered tool call, or emitted only reasoning. It does not rank; rerun it.",
			)}
			testId={testId ?? "benchmark-incomplete"}
		/>
	);
}

export function BenchmarkJudgeStateBadge({ state }: { state: BenchmarkJudgeState }) {
	const { t } = useTranslation();
	const label = t(`pages.benchmarks.judgeState.${state}`, state);
	return (
		<StatusBadge
			color={judgeColors[state]}
			label={label}
			inProgress={isJudgeActive(state)}
			aria-label={label}
			data-testid="benchmark-judge-state"
		/>
	);
}
