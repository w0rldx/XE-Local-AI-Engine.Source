import { Alert, Stack } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { BenchmarkEvidenceDiffTable, BenchmarkEvidenceTable } from "@/features/benchmarks/components/BenchmarkEvidenceTable";
import type { BenchmarkEvidenceDiffRow } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import { flattenEvidence } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkEvidenceObject, BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";

interface EvidenceBlockProps {
	title: string;
	evidence: BenchmarkEvidenceObject | null;
	/** Root of the dotted field paths; the same roots the compare diff uses, so field names read identically. */
	prefix: string;
	testId: string;
}

function EvidenceBlock({ title, evidence, prefix, testId }: EvidenceBlockProps) {
	if (evidence === null) {
		return null;
	}
	return (
		<SectionCard title={title} gap="sm">
			<BenchmarkEvidenceTable entries={flattenEvidence(evidence, prefix)} data-testid={testId} />
		</SectionCard>
	);
}

// The durable launch evidence of one run: what the freeze intended, what the provider reported after readiness, and the
// environment captured just before the spawn. Nothing here is a judgement — differences are shown, not interpreted.
export function BenchmarkLaunchEvidencePanel({ run }: { run: BenchmarkRunDetail }) {
	const { t } = useTranslation();
	const launch = run.primaryLaunch;
	const intendedDiffers =
		launch.intendedLaunchIdentity !== null &&
		launch.effectiveLaunchIdentity !== null &&
		launch.intendedLaunchIdentity !== launch.effectiveLaunchIdentity;
	const intendedRows: BenchmarkEvidenceDiffRow[] = [
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

	return (
		<Stack gap="sm">
			{intendedDiffers ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-intended-effective-differs">
					<Stack gap="xs">
						{t("pages.benchmarks.launch.intendedDiffers", "The intended launch and the effective launch differ.")}
						<BenchmarkEvidenceDiffTable
							rows={intendedRows}
							leftLabel={t("pages.benchmarks.launch.intended", "Intended")}
							rightLabel={t("pages.benchmarks.launch.effective", "Effective")}
							data-testid="benchmark-intended-effective-table"
						/>
					</Stack>
				</Alert>
			) : null}
			<EvidenceBlock
				title={t("pages.benchmarks.launch.receipt", "Launch receipt")}
				evidence={run.primaryLaunchReceipt}
				prefix="receipt"
				testId="benchmark-primary-receipt"
			/>
			<EvidenceBlock
				title={t("pages.benchmarks.launch.environment", "Environment")}
				evidence={run.primaryEnvironmentFacts}
				prefix="environment"
				testId="benchmark-primary-environment"
			/>
			<EvidenceBlock
				title={t("pages.benchmarks.launch.judgeReceipt", "Judge launch receipt")}
				evidence={run.judgeLaunchReceipt}
				prefix="receipt"
				testId="benchmark-judge-receipt"
			/>
			<EvidenceBlock
				title={t("pages.benchmarks.launch.judgeEnvironment", "Judge environment")}
				evidence={run.judgeEnvironmentFacts}
				prefix="environment"
				testId="benchmark-judge-environment"
			/>
		</Stack>
	);
}
