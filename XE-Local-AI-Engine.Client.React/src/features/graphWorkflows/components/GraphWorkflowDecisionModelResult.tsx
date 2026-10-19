// A DecisionModel node run's answer, read out of its output document: the chosen label for `choice`, the continuous
// score and its nearest level for `score`, P(yes) for `noul`, plus whatever probabilities and confidence the
// `systemone` provider reported. Nothing is derived that the server did not send.

import { Group, Progress, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { readGraphWorkflowDecisionModelOutput } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export function GraphWorkflowDecisionModelResult({ output }: { readonly output: unknown }) {
	const { t, i18n } = useTranslation();
	const result = readGraphWorkflowDecisionModelOutput(output);
	if (!result) {
		return null;
	}
	const percent = new Intl.NumberFormat(i18n.language, { style: "percent", maximumFractionDigits: 1 });
	const decimal = new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 2 });
	const levelName = (key: string): string => result.legend?.[key] ?? key;
	// A choice reads best most-likely first; score levels are ordinal, so they keep the server's (lowest-first) order.
	const probabilities = Object.entries(result.probabilities ?? {});
	if (result.type === "choice") {
		probabilities.sort((left, right) => right[1] - left[1]);
	}
	const modelReported = result.probabilities !== null || result.confidence !== null || result.noul !== null;

	return (
		<Stack gap={6} data-testid="graph-workflow-node-panel-decision">
			{result.type === "choice" && result.choice !== null ? (
				<Text size="sm" data-testid="graph-workflow-node-panel-choice">
					{t("pages.graphWorkflows.nodePanel.decisionChoice", "Chose {{choice}}", { choice: result.choice })}
				</Text>
			) : null}
			{result.type === "score" && result.score !== null ? (
				<Text size="sm" data-testid="graph-workflow-node-panel-score">
					{t("pages.graphWorkflows.nodePanel.decisionScore", "Score {{score}}, nearest level {{level}}", {
						score: decimal.format(result.score),
						level: result.choice ?? "—",
					})}
				</Text>
			) : null}
			{result.type === "noul" && result.noul !== null ? (
				<Text size="sm" data-testid="graph-workflow-node-panel-noul">
					{/* `answer` is the routing literal the server wrote into output.choice, shown verbatim: a translated
					    "ja" would not match an `Eq "yes"` edge, and recomputing it here could disagree with the server. */}
					{result.choice === null
						? t("pages.graphWorkflows.nodePanel.decisionNoulOnly", "P(yes) {{probability}}", {
								probability: percent.format(result.noul),
							})
						: t("pages.graphWorkflows.nodePanel.decisionNoul", "P(yes) {{probability}}: {{answer}}", {
								probability: percent.format(result.noul),
								answer: result.choice,
							})}
				</Text>
			) : null}
			{result.type === undefined ? (
				<Text size="sm" c="dimmed" data-testid="graph-workflow-node-panel-decision-unsupported">
					{t(
						"pages.graphWorkflows.nodePanel.decisionUnsupportedType",
						"This version cannot show this result type. The raw output is below.",
					)}
				</Text>
			) : null}
			{result.confidence !== null ? (
				<Text size="xs" c="dimmed" data-testid="graph-workflow-node-panel-confidence">
					{t("pages.graphWorkflows.nodePanel.decisionConfidence", "Confidence {{confidence}}", {
						confidence: percent.format(result.confidence),
					})}
				</Text>
			) : null}
			{result.type === "score" && result.legend !== null ? (
				<Stack gap={0} data-testid="graph-workflow-node-panel-legend">
					{Object.entries(result.legend).map(([level, name]) => (
						<Text key={level} size="xs" c="dimmed">
							{`${level}: ${name}`}
						</Text>
					))}
				</Stack>
			) : null}
			{probabilities.length > 0 ? (
				<Stack gap={4} data-testid="graph-workflow-node-panel-probabilities">
					{probabilities.map(([key, probability]) => (
						<Group key={key} gap="xs" wrap="nowrap" data-testid={`graph-workflow-node-panel-probability-${key}`}>
							<Text size="xs" style={{ width: "40%", minWidth: 0 }} truncate={true}>
								{levelName(key)}
							</Text>
							<Progress
								value={Math.min(100, Math.max(0, probability * 100))}
								size="sm"
								style={{ flex: 1 }}
								aria-label={levelName(key)}
							/>
							<Text size="xs" c="dimmed" style={{ width: "4.5em", textAlign: "right" }}>
								{percent.format(probability)}
							</Text>
						</Group>
					))}
				</Stack>
			) : null}
			{modelReported ? (
				<Text size="xs" c="dimmed" fs="italic" data-testid="graph-workflow-node-panel-uncalibrated">
					{t("pages.graphWorkflows.nodePanel.decisionUncalibrated", "Model-reported, uncalibrated.")}
				</Text>
			) : null}
		</Stack>
	);
}
