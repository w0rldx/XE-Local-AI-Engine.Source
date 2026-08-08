import { Badge, Button, Collapse, Group, Stack, Text, Title } from "@mantine/core";
import { IconChevronDown, IconChevronUp } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import type { ModelFitLatestRecommendations, ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

interface RecommendationSnapshotProps {
	readonly latest: ModelFitLatestRecommendations;
	readonly onDownload: (recommendation: ModelFitRecommendation) => void;
	readonly downloadingModelName: string | null;
}

// Renders a populated recommendation snapshot: the status / use-case summary, then the ranked list split into the three
// backend sections — hardware-confident "recommended" picks, reduced-quality "can run" picks (collapsed by default,
// with a count on the toggle), and trending "explore" catalog entries. Empty sections are hidden entirely; an all-empty
// snapshot shows the empty-list note. The parent owns the loading / error / no-cache states.
export function RecommendationSnapshot({ latest, onDownload, downloadingModelName }: RecommendationSnapshotProps) {
	const { t } = useTranslation();
	// "Can run (reduced quality)" is collapsed by default — it's the noisier, lower-confidence group.
	const [canRunOpen, setCanRunOpen] = useState(false);

	// Split the one flat ranked list into the three sections the backend returns.
	const { recommendedRows, canRunRows, exploreRows } = useMemo(
		() => ({
			recommendedRows: latest.recommendations.filter((row) => row.section === "recommended"),
			canRunRows: latest.recommendations.filter((row) => row.section === "canRun"),
			exploreRows: latest.recommendations.filter((row) => row.section === "explore"),
		}),
		[latest.recommendations],
	);

	return (
		<Stack gap="md" data-testid="model-fit-snapshot">
			<Group gap="xl">
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.modelFit.recommendations.snapshot.status", "Status")}
					</Text>
					<Badge variant="light">{latest.status ?? "—"}</Badge>
				</Stack>
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.modelFit.recommendations.snapshot.useCase", "Use case")}
					</Text>
					<Text size="sm">
						{latest.useCase ? t(`pages.modelFit.recommendations.useCases.${latest.useCase}`, latest.useCase) : "—"}
					</Text>
				</Stack>
			</Group>

			{latest.recommendations.length > 0 ? (
				<Stack gap="lg">
					{recommendedRows.length > 0 ? (
						<Stack gap="xs" data-testid="model-fit-section-recommended">
							<Title order={4}>
								{t("pages.modelFit.recommendations.sections.recommended", "Recommended for your hardware")}
							</Title>
							<RecommendationTable
								recommendations={recommendedRows}
								onDownload={onDownload}
								downloadingModelName={downloadingModelName}
							/>
						</Stack>
					) : null}

					{canRunRows.length > 0 ? (
						<Stack gap="xs" data-testid="model-fit-section-can-run">
							<Button
								variant="subtle"
								color="gray"
								size="sm"
								px={0}
								leftSection={canRunOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
								onClick={() => setCanRunOpen((current) => !current)}
								data-testid="model-fit-section-can-run-toggle"
							>
								{t("pages.modelFit.recommendations.sections.canRunCount", "Can run ({{count}})", {
									count: canRunRows.length,
								})}
							</Button>
							<Collapse expanded={canRunOpen}>
								<RecommendationTable
									recommendations={canRunRows}
									onDownload={onDownload}
									downloadingModelName={downloadingModelName}
								/>
							</Collapse>
						</Stack>
					) : null}

					{exploreRows.length > 0 ? (
						<Stack gap="xs" data-testid="model-fit-section-explore">
							<Title order={4}>
								{t("pages.modelFit.recommendations.sections.explore", "Explore trending on Hugging Face")}
							</Title>
							<RecommendationTable
								recommendations={exploreRows}
								onDownload={onDownload}
								downloadingModelName={downloadingModelName}
							/>
						</Stack>
					) : null}
				</Stack>
			) : (
				<Text c="dimmed" data-testid="model-fit-recommendations-empty-list">
					{t("pages.modelFit.recommendations.emptyList", "The latest snapshot returned no recommendations.")}
				</Text>
			)}
		</Stack>
	);
}
