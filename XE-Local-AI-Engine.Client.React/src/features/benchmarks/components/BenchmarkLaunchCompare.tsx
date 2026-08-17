import { Alert, Group, Stack, Text } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
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
import { throughputEvidenceEntries } from "@/features/benchmarks/models/BenchmarkThroughput";
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

// Throughput is compared with the SAME diff machinery as launch evidence rather than a second implementation, and for
// the same reason: the table reports what differs and never interprets it. Two runs differing in tok/s says nothing
// about which is the better answer — throughput is display only and ranks nothing.
function throughputRows(left: BenchmarkRunDetail, right: BenchmarkRunDetail): BenchmarkEvidenceDiffRow[] {
	return diffLaunchEvidence(throughputEvidenceEntries(left.throughput), throughputEvidenceEntries(right.throughput));
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
	const throughput = useMemo(() => (left && right ? throughputRows(left, right) : []), [left, right]);

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
	// Driven by the computed rows, not by the hashes: neither hash covers the freeze-side facts (KV source, auto
	// reason, intended identity), so a hash comparison would stay silent on a difference the table already shows.
	const primaryDiffers = primary.some((row) => row.differs);
	const throughputDiffers = throughput.some((row) => row.differs);

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
			{throughputDiffers ? (
				<Alert color="gray" icon={<IconInfoCircle size={16} />} data-testid="benchmark-throughput-differs">
					<Stack gap="xs">
						<Text size="sm">
							{t("pages.benchmarks.metrics.throughputDiffers", "Throughput differs: {{fields}}", { fields: fieldList(throughput) })}
						</Text>
						<BenchmarkEvidenceDiffTable
							rows={throughput}
							leftLabel={leftLabel}
							rightLabel={rightLabel}
							data-testid="benchmark-throughput-diff"
						/>
					</Stack>
				</Alert>
			) : null}
		</Stack>
	);
}
