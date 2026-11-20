import { SegmentedControl, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesModelLabModelLabResultToken as ResultToken } from "@/core/api/generated/types.gen";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { type CandidateBasis, sameCandidates, toStepEntry } from "@/features/modelLab/models/ModelLabTrace";

interface TraceStepCandidatesProps {
	token: ResultToken;
	// 0-based.
	step: number;
	basis: CandidateBasis;
	onBasisChange: (basis: CandidateBasis) => void;
}

// One traced step's exact candidates, raw or processed; null when the trace was made before candidates were recorded.
export function TraceStepCandidates({ token, step, basis, onBasisChange }: TraceStepCandidatesProps) {
	const { t } = useTranslation();
	const entry = toStepEntry(token, basis);
	if (!entry) {
		return null;
	}
	return (
		<Stack gap="xs" data-testid="model-lab-trace-candidates">
			<SegmentedControl
				value={basis}
				onChange={(value) => onBasisChange(value === "processed" ? "processed" : "raw")}
				aria-label={t("pages.modelLab.trace.basisLabel", "Candidates from")}
				data={[
					{ value: "raw", label: t("pages.modelLab.trace.basisRaw", "Raw logits") },
					{ value: "processed", label: t("pages.modelLab.trace.basisProcessed", "Processed scores") },
				]}
				data-testid="model-lab-trace-basis"
			/>
			{sameCandidates(token) && (
				<Text size="xs" c="dimmed" data-testid="model-lab-trace-same-candidates">
					{t(
						"pages.modelLab.trace.sameCandidates",
						"Raw and processed candidates are identical for this step: the generation config's processors changed nothing among these candidates.",
					)}
				</Text>
			)}
			<NextTokenStepView
				entry={entry}
				stepNumber={step + 1}
				trace={{
					processed: basis === "processed",
					basis:
						basis === "raw"
							? t(
									"pages.modelLab.trace.basisRawNote",
									"Computed by PyTorch from the model's raw logits over the whole vocabulary",
								)
							: t(
									"pages.modelLab.trace.basisProcessedNote",
									"Computed by PyTorch from the processed scores, after the checkpoint's generation config processors",
								),
				}}
			/>
		</Stack>
	);
}
