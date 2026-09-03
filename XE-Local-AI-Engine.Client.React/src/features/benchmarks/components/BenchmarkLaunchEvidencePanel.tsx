import { Accordion, Alert, Stack, Text } from "@mantine/core";
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
	// A row frozen under a superseded launch-identity scheme carries two identities that were never meant to be
	// compared, so the identity row is never a difference and the alert says so in one line instead of raising a drift
	// warning about a launch that in fact matched. The flag is common, not exotic: the server computes it as "stored
	// scheme, NULL read as 1, != the current version", which is true of every run frozen before the cutover. Only the
	// identity is scheme-bound — the executable digest is compared either way, because a digest is a digest under any
	// scheme.
	const schemeOutdated = launch.launchIdentitySchemeOutdated === true;
	const rows: BenchmarkEvidenceDiffRow[] = [
		{
			key: "launch.launchIdentity",
			values: [launch.intendedLaunchIdentity, launch.effectiveLaunchIdentity],
			differs: !schemeOutdated && launch.intendedLaunchIdentity !== launch.effectiveLaunchIdentity,
		},
		{
			key: "launch.executableSha256",
			values: [launch.intendedExecutableSha256, launch.executableSha256],
			differs: launch.intendedExecutableSha256 !== launch.executableSha256,
		},
	];
	if (!rows.some((row) => row.differs && row.values.every((value) => value !== null))) {
		return null;
	}
	return (
		<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid={testId}>
			<Stack gap="xs">
				{message}
				{schemeOutdated ? (
					<Text size="sm" c="dimmed" data-testid={`${testId}-scheme-outdated`}>
						{t(
							"pages.benchmarks.launch.identitySchemeOutdated",
							"Frozen under an earlier launch-identity scheme, so the two identities are not comparable.",
						)}
					</Text>
				) : null}
				<BenchmarkEvidenceDiffTable
					rows={rows}
					labels={[t("pages.benchmarks.launch.intended", "Intended"), t("pages.benchmarks.launch.effective", "Effective")]}
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
