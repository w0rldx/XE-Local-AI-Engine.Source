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
 * reader from taking a fragment for a finished answer.
 */
export function BenchmarkTruncatedBadge({ testId }: { testId?: string }) {
	const { t } = useTranslation();
	const label = t("pages.benchmarks.status.truncated", "Truncated");
	return (
		<Tooltip
			label={t(
				"pages.benchmarks.status.truncatedHint",
				"The answer was cut off by the token budget. It does not rank — rerun with a larger context or output budget.",
			)}
			multiline={true}
			w={260}
		>
			<span>
				<StatusBadge color="orange" label={label} aria-label={label} data-testid={testId ?? "benchmark-truncated"} />
			</span>
		</Tooltip>
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
