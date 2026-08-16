import { Accordion, Alert, Stack } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkEvidenceDiffTable, BenchmarkEvidenceTable } from "@/features/benchmarks/components/BenchmarkEvidenceTable";
import type { BenchmarkEvidenceDiffRow } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import { flattenEvidence } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type {
	BenchmarkEvidenceObject,
	BenchmarkLaunchFacts,
	BenchmarkRunDetail,
} from "@/features/benchmarks/models/BenchmarkModels";

interface IntendedEffectiveAlertProps {
	launch: BenchmarkLaunchFacts;
	message: string;
	testId: string;
}

// Both recorded facts are compared, not just the identity: an executable whose digest moved between freeze and spawn is
// a real drift even when the projection hashed the same. A side that recorded only one half cannot be compared, so a
// row with a missing end is not a difference.
function IntendedEffectiveAlert({ launch, message, testId }: IntendedEffectiveAlertProps) {
	const { t } = useTranslation();
	const rows: BenchmarkEvidenceDiffRow[] = [
		{
			key: "launch.launchIdentity",
			left: launch.intendedLaunchIdentity,
			right: launch.effectiveLaunchIdentity,
			differs: launch.intendedLaunchIdentity !== launch.effectiveLaunchIdentity,
		},
		{
			key: "launch.executableSha256",
			left: launch.intendedExecutableSha256,
			right: launch.executableSha256,
			differs: launch.intendedExecutableSha256 !== launch.executableSha256,
		},
	];
	if (!rows.some((row) => row.differs && row.left !== null && row.right !== null)) {
		return null;
	}
	return (
		<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid={testId}>
			<Stack gap="xs">
				{message}
				<BenchmarkEvidenceDiffTable
					rows={rows}
					leftLabel={t("pages.benchmarks.launch.intended", "Intended")}
					rightLabel={t("pages.benchmarks.launch.effective", "Effective")}
					data-testid={`${testId}-table`}
				/>
			</Stack>
		</Alert>
	);
}

// The durable launch evidence of one run's PRIMARY side: what the freeze intended, what the provider reported after
// readiness, and
// the environment captured just before the spawn. The judge's own evidence belongs to its attempt and is not
// projected onto the run. Nothing here is a judgement — differences are shown, not interpreted.
// The evidence objects sit behind a collapsed accordion because a runtime-bundle listing runs to hundreds of rows.
export function BenchmarkLaunchEvidencePanel({ run }: { run: BenchmarkRunDetail }) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState<string[]>([]);
	const blocks: { value: string; title: string; evidence: BenchmarkEvidenceObject | null; prefix: string }[] = [
		{
			value: "primary-receipt",
			title: t("pages.benchmarks.launch.receipt", "Launch receipt"),
			evidence: run.primaryLaunchReceipt,
			prefix: "receipt",
		},
		{
			value: "primary-environment",
			title: t("pages.benchmarks.launch.environment", "Environment"),
			evidence: run.primaryEnvironmentFacts,
			prefix: "environment",
		},
	];
	const present = blocks.filter((block) => block.evidence !== null);

	return (
		<Stack gap="sm">
			<IntendedEffectiveAlert
				launch={run.primaryLaunch}
				message={t("pages.benchmarks.launch.intendedDiffers", "The intended launch and the effective launch differ.")}
				testId="benchmark-intended-effective-differs"
			/>
			{present.length > 0 ? (
				<Accordion multiple={true} variant="contained" value={opened} onChange={setOpened}>
					{present.map((block) => (
						<Accordion.Item key={block.value} value={block.value}>
							<Accordion.Control>{block.title}</Accordion.Control>
							<Accordion.Panel>
								{opened.includes(block.value) ? (
									<BenchmarkEvidenceTable
										entries={flattenEvidence(block.evidence, block.prefix)}
										data-testid={`benchmark-${block.value}`}
									/>
								) : null}
							</Accordion.Panel>
						</Accordion.Item>
					))}
				</Accordion>
			) : null}
		</Stack>
	);
}
