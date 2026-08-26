import { Alert, Group, Stack, Text } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkEvidenceDiffTable } from "@/features/benchmarks/components/BenchmarkEvidenceTable";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { fidelityEvidenceEntries } from "@/features/benchmarks/models/BenchmarkFidelity";
import type { BenchmarkEvidenceDiffRow } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import {
	differingEvidenceKeys,
	diffLaunchEvidence,
	launchEvidenceEntries,
} from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { throughputEvidenceEntries } from "@/features/benchmarks/models/BenchmarkThroughput";
import { useBenchmarkRunDetails } from "@/features/benchmarks/queries/useBenchmarks";

const maxListedFields = 6;

/**
 * The run's model name, disambiguated by id only where the name repeats — which it does exactly when the operator is
 * comparing repeats of one build, the case where a bare name would make every column look the same.
 */
function columnLabels(runs: readonly BenchmarkRunDetail[]): string[] {
	const nameCounts = new Map<string, number>();
	for (const run of runs) {
		nameCounts.set(run.primaryModelName, (nameCounts.get(run.primaryModelName) ?? 0) + 1);
	}
	return runs.map((run) =>
		(nameCounts.get(run.primaryModelName) ?? 0) > 1 ? `${run.primaryModelName} · ${run.id.slice(0, 8)}` : run.primaryModelName,
	);
}

const primaryRows = (runs: readonly BenchmarkRunDetail[]): BenchmarkEvidenceDiffRow[] =>
	diffLaunchEvidence(
		runs.map((run) => launchEvidenceEntries(run.primaryLaunch, run.primaryLaunchReceipt, run.primaryEnvironmentFacts)),
	);

// Throughput and fidelity are compared with the SAME diff machinery as launch evidence rather than a second
// implementation, and for the same reason: the table reports what differs and never interprets it. Two runs differing
// in tok/s — or in perplexity — says nothing about which gave the better answer. Both axes are display only.
const throughputRows = (runs: readonly BenchmarkRunDetail[]): BenchmarkEvidenceDiffRow[] =>
	diffLaunchEvidence(runs.map((run) => throughputEvidenceEntries(run.throughput)));

const fidelityRows = (runs: readonly BenchmarkRunDetail[]): BenchmarkEvidenceDiffRow[] =>
	diffLaunchEvidence(runs.map((run) => fidelityEvidenceEntries(run.fidelity)));

/**
 * Launch, throughput and fidelity evidence of the selected runs side by side, one column each. Differences are
 * reported as facts and never interpreted: the copy says *what* differs, never whether the runs may be ranked against
 * each other; this surface does not make that judgement.
 *
 * Two runs is the ordinary case and stays a two-column table. The cap on how many columns may be asked for lives with
 * the caller: "how many fit on the page" is a page decision, "what differs across them" is this one.
 */
export function BenchmarkLaunchCompare({ runIds }: { runIds: readonly string[] }) {
	const { t } = useTranslation();
	const { runs } = useBenchmarkRunDetails(runIds);
	// Every selected run must have arrived before anything is compared: a table missing a column would highlight rows
	// as differing when the absent run may well have matched.
	const compared = useMemo<readonly BenchmarkRunDetail[]>(
		() => (runs.length === runIds.length ? runs : []),
		[runs, runIds.length],
	);
	const primary = useMemo(() => (compared.length > 1 ? primaryRows(compared) : []), [compared]);
	const throughput = useMemo(() => (compared.length > 1 ? throughputRows(compared) : []), [compared]);
	const fidelity = useMemo(() => (compared.length > 1 ? fidelityRows(compared) : []), [compared]);

	if (compared.length < 2) {
		return null;
	}

	const labels = columnLabels(compared);
	const fieldList = (rows: readonly BenchmarkEvidenceDiffRow[]): string => {
		const keys = differingEvidenceKeys(rows);
		return keys.length > maxListedFields
			? `${keys.slice(0, maxListedFields).join(", ")} (+${keys.length - maxListedFields})`
			: keys.join(", ");
	};
	// Driven by the computed rows, not by the hashes: neither hash covers the freeze-side facts (KV source, auto
	// reason, intended identity), so a hash comparison would stay silent on a difference the table already shows.
	const sections = [
		{
			rows: primary,
			color: "blue",
			testId: "primary-launch",
			message: t("pages.benchmarks.launch.primaryDiffers", "Launch differs: {{fields}}", { fields: fieldList(primary) }),
		},
		{
			rows: throughput,
			color: "gray",
			testId: "throughput",
			message: t("pages.benchmarks.metrics.throughputDiffers", "Throughput differs: {{fields}}", {
				fields: fieldList(throughput),
			}),
		},
		{
			rows: fidelity,
			color: "gray",
			testId: "fidelity",
			message: t("pages.benchmarks.fidelity.differs", "Quant fidelity differs: {{fields}}", { fields: fieldList(fidelity) }),
		},
	];

	return (
		<Stack gap="sm" data-testid="benchmark-launch-compare">
			<Text size="sm" c="dimmed" data-testid="benchmark-compare-count">
				{t("pages.benchmarks.rank.comparing", "Comparing {{count}} runs", { count: compared.length })}
			</Text>
			{compared.map((run, index) => (
				<Group key={run.id} gap="sm" align="center">
					<Text size="sm" fw={600}>
						{labels[index]}
					</Text>
					<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid={`benchmark-launch-line-${run.id}`} />
				</Group>
			))}
			{sections
				.filter((section) => section.rows.some((row) => row.differs))
				.map((section) => (
					<Alert
						key={section.testId}
						color={section.color}
						icon={<IconInfoCircle size={16} />}
						data-testid={`benchmark-${section.testId}-differs`}
					>
						<Stack gap="xs">
							<Text size="sm">{section.message}</Text>
							<BenchmarkEvidenceDiffTable
								rows={section.rows}
								labels={labels}
								data-testid={`benchmark-${section.testId}-diff`}
							/>
						</Stack>
					</Alert>
				))}
		</Stack>
	);
}
