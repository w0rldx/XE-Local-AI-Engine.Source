import { Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { type BlockTraceSummary, formatTraceValue } from "@/features/modelLab/models/ModelLabArchitecture";

interface ArchitectureBlockTraceProps {
	untraceable: boolean;
	// Undefined while no trace is chosen.
	summaries: ReadonlyMap<number, BlockTraceSummary> | undefined;
	blockIndex: number;
}

// What the chosen trace measured on this block, or why it has nothing to say.
export function ArchitectureBlockTrace({ untraceable, summaries, blockIndex }: ArchitectureBlockTraceProps) {
	const { t, i18n } = useTranslation();
	const formatValue = (value: number | null): string => formatTraceValue(value, i18n.language);
	if (untraceable) {
		return (
			<Text size="sm" c="dimmed" data-testid="model-lab-architecture-untraceable">
				{t(
					"pages.modelLab.architecture.untraceable",
					"Not traceable: this multi-token prediction block has no layer in the Hugging Face checkpoint.",
				)}
			</Text>
		);
	}
	if (summaries === undefined) {
		return null;
	}
	const summary = summaries.get(blockIndex);
	if (summary === undefined) {
		return (
			<Text size="sm" c="dimmed" data-testid="model-lab-architecture-not-traced">
				{t("pages.modelLab.architecture.notTraced", "Not traced: the chosen trace did not measure this block.")}
			</Text>
		);
	}
	return (
		<Text size="sm" data-testid="model-lab-architecture-trace-summary">
			{t(
				"pages.modelLab.architecture.traceSummary",
				"Mean norm {{mean}}, highest p99 {{p99}}. Most frequent top dimensions: {{dimensions}}.",
				{
					mean: formatValue(summary.meanNorm),
					p99: formatValue(summary.maxP99),
					dimensions: summary.topDimensions.length > 0 ? summary.topDimensions.join(", ") : "–",
				},
			)}
		</Text>
	);
}
