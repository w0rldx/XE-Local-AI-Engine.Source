import { SegmentedControl, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesModelLabModelLabResultToken as ResultToken } from "@/core/api/generated/types.gen";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import {
	type CandidateBasis,
	type CandidateNote,
	candidateNote,
	sameCandidates,
	toStepEntry,
} from "@/features/modelLab/models/ModelLabTrace";

interface TraceStepCandidatesProps {
	token: ResultToken;
	// 0-based.
	step: number;
	// The result's count of leading steps that kept their candidates; null or undefined on a result made before it was recorded.
	candidateSteps: number | null | undefined;
	basis: CandidateBasis;
	onBasisChange: (basis: CandidateBasis) => void;
}

// Why a step has no candidates to show; nothing when it has them.
export function CandidateNoteText({ note }: { note: CandidateNote }) {
	const { t } = useTranslation();
	if (note === "kept") {
		return null;
	}
	return (
		<Text size="sm" c="dimmed" data-testid={`model-lab-candidates-${note}`}>
			{note === "notKept"
				? t(
						"pages.modelLab.trace.candidatesNotKept",
						"This step's candidates were not kept: a long run keeps them only for its first steps, so its saved result stays within its size limit. The token, its text and its probability are kept.",
					)
				: t("pages.modelLab.trace.noCandidates", "This trace recorded no candidates (made before this version).")}
		</Text>
	);
}

// One traced step's exact candidates, raw or processed; otherwise why there are none.
export function TraceStepCandidates({ token, step, candidateSteps, basis, onBasisChange }: TraceStepCandidatesProps) {
	const { t } = useTranslation();
	const entry = toStepEntry(token, basis);
	if (!entry) {
		return <CandidateNoteText note={candidateNote(token, step, candidateSteps)} />;
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
