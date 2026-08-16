import { Alert, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconInfoCircle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkEvidenceDiffTable } from "@/features/benchmarks/components/BenchmarkEvidenceTable";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import type { BenchmarkEvidenceDiffRow } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import {
	differingEvidenceKeys,
	diffLaunchEvidence,
	launchEvidenceEntries,
} from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { useBenchmarkRun } from "@/features/benchmarks/queries/useBenchmarks";

const maxListedFields = 6;

const sideLabel = (run: BenchmarkRunDetail, other: BenchmarkRunDetail): string =>
	run.primaryModelName === other.primaryModelName ? `${run.primaryModelName} · ${run.id.slice(0, 8)}` : run.primaryModelName;

function primaryRows(left: BenchmarkRunDetail, right: BenchmarkRunDetail): BenchmarkEvidenceDiffRow[] {
	return diffLaunchEvidence(
		launchEvidenceEntries(left.primaryLaunch, left.primaryLaunchReceipt, left.primaryEnvironmentFacts),
		launchEvidenceEntries(right.primaryLaunch, right.primaryLaunchReceipt, right.primaryEnvironmentFacts),
	);
}

function judgeRows(left: BenchmarkRunDetail, right: BenchmarkRunDetail): BenchmarkEvidenceDiffRow[] {
	return diffLaunchEvidence(
		launchEvidenceEntries(left.judgeLaunch, left.judgeLaunchReceipt, left.judgeEnvironmentFacts),
		launchEvidenceEntries(right.judgeLaunch, right.judgeLaunchReceipt, right.judgeEnvironmentFacts),
	);
}

/**
 * Launch evidence of the two selected runs side by side. Differences are reported as facts and never interpreted: the
 * copy says *what* differs, never whether the two runs may be ranked against each other (D12) — that judgement is not
 * this plan's to make.
 */
export function BenchmarkLaunchCompare({ leftRunId, rightRunId }: { leftRunId: string; rightRunId: string }) {
	const { t } = useTranslation();
	const leftQuery = useBenchmarkRun(leftRunId);
	const rightQuery = useBenchmarkRun(rightRunId);
	const left = leftQuery.data;
	const right = rightQuery.data;
	const primary = useMemo(() => (left && right ? primaryRows(left, right) : []), [left, right]);
	const judge = useMemo(() => (left && right ? judgeRows(left, right) : []), [left, right]);

	if (!left || !right) {
		return null;
	}

	const leftLabel = sideLabel(left, right);
	const rightLabel = sideLabel(right, left);
	const fieldList = (rows: readonly BenchmarkEvidenceDiffRow[]): string => {
		const keys = differingEvidenceKeys(rows);
		return keys.length > maxListedFields
			? `${keys.slice(0, maxListedFields).join(", ")} (+${keys.length - maxListedFields})`
			: keys.join(", ");
	};
	const primaryDiffers = !Object.is(left.primaryLaunch.receiptHash, right.primaryLaunch.receiptHash);
	const judgeDiffers = !Object.is(left.judgeLaunch.receiptHash, right.judgeLaunch.receiptHash);

	return (
		<Stack gap="sm" data-testid="benchmark-launch-compare">
			{[
				{ run: left, label: leftLabel },
				{ run: right, label: rightLabel },
			].map(({ run, label }) => (
				<Group key={run.id} gap="sm" align="center">
					<Text size="sm" fw={600}>
						{label}
					</Text>
					<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid={`benchmark-launch-line-${run.id}`} />
				</Group>
			))}
			{primaryDiffers ? (
				<Alert color="blue" icon={<IconInfoCircle size={16} />} data-testid="benchmark-primary-launch-differs">
					<Stack gap="xs">
						<Text size="sm">
							{t("pages.benchmarks.launch.primaryDiffers", "Launch differs: {{fields}}", { fields: fieldList(primary) })}
						</Text>
						<BenchmarkEvidenceDiffTable
							rows={primary}
							leftLabel={leftLabel}
							rightLabel={rightLabel}
							data-testid="benchmark-primary-launch-diff"
						/>
					</Stack>
				</Alert>
			) : null}
			{judgeDiffers ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-judge-launch-differs">
					<Stack gap="xs">
						<Text size="sm">
							{t("pages.benchmarks.launch.judgeDiffers", "Judge launch/environment differs: {{fields}}", {
								fields: fieldList(judge),
							})}
						</Text>
						<BenchmarkEvidenceDiffTable
							rows={judge}
							leftLabel={leftLabel}
							rightLabel={rightLabel}
							data-testid="benchmark-judge-launch-diff"
						/>
					</Stack>
				</Alert>
			) : null}
		</Stack>
	);
}
