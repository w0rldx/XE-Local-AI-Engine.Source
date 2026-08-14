import { Alert, Card, Group, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconScale } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { BenchmarkStatusBadge } from "@/features/benchmarks/components/BenchmarkStatusBadge";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";

export function BenchmarkJudgePanel({ run }: { run: BenchmarkRunDetail }) {
	const { t } = useTranslation();
	const explanation = t(
		`pages.benchmarks.judge.explanations.${run.judgeStatus}`,
		run.judgeStatus === "Disabled"
			? "Automated judging was not requested for this project."
			: run.judgeStatus === "Skipped"
				? "Judging was requested but skipped because the primary run did not succeed."
				: "The judge lifecycle is independent from the primary result.",
	);
	return (
		<Card withBorder={true} radius="md" padding="md" data-testid="benchmark-judge-panel">
			<Stack gap="sm">
				<Group justify="space-between">
					<Group gap="xs">
						<IconScale size={18} />
						<Title order={5}>{t("pages.benchmarks.judge.title", "Automated judge")}</Title>
					</Group>
					<BenchmarkStatusBadge status={run.judgeStatus} />
				</Group>
				<Text size="sm" c="dimmed">
					{explanation}
				</Text>
				{run.judgeResult ? (
					<Stack gap={4}>
						<Text fw={700}>{t("pages.benchmarks.judge.score", "Judge score: {{score}}", { score: run.judgeResult.score })}</Text>
						<Text size="sm">{run.judgeResult.rationale}</Text>
					</Stack>
				) : null}
				{run.judgeStatus === "Failed" && run.judgeErrorMessage ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{run.judgeErrorMessage}
					</Alert>
				) : null}
			</Stack>
		</Card>
	);
}
