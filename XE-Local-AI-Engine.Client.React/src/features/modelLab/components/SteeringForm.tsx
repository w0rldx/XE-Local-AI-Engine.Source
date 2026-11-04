import { Button, Group, NumberInput, Stack, Text, Textarea } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesModelLabModelLabSteeringRequest as SteeringRequest } from "@/core/api/generated/types.gen";
import {
	layerKindLabel,
	layerKindOf,
	promptLines,
	type SteeringDraft,
	steeringProblem,
} from "@/features/modelLab/models/ModelLabSteering";

export interface SteeringStart {
	prompt: string;
	steering: SteeringRequest;
}

interface SteeringFormProps {
	defaultLayer: number | null | undefined;
	steerableBlockCount: number | null | undefined;
	layerTypes: string[] | null | undefined;
	draft: SteeringDraft;
	onDraftChange: (draft: SteeringDraft) => void;
	disabled: boolean;
	loading: boolean;
	onStart: (start: SteeringStart) => void;
}

// The two contrast sets, the test prompts, the layer and the scale. The server decides what is allowed: a refused layer comes back as a message.
export function SteeringForm({
	defaultLayer,
	steerableBlockCount,
	layerTypes,
	draft,
	onDraftChange,
	disabled,
	loading,
	onStart,
}: SteeringFormProps) {
	const { t } = useTranslation();
	const { positive, negative, tests, layer, scale } = draft;
	const problem = steeringProblem(t, draft);

	const testLines = promptLines(tests);
	const positiveLines = promptLines(positive);
	const negativeLines = promptLines(negative);
	const chosenLayer = typeof layer === "number" ? layer : (defaultLayer ?? undefined);
	const [firstTest, ...moreTests] = testLines;
	const ready = problem === null && !disabled;

	return (
		<Stack gap="sm" data-testid="model-lab-steering-form">
			<Text size="sm" c="dimmed">
				{t(
					"pages.modelLab.steer.intro",
					"Derives a vector from the two sets, then runs the GGUF three times on the same prompts and seed: no vector, this vector, and a random one of the same length. One prompt per line.",
				)}
			</Text>
			<Textarea
				label={t("pages.modelLab.steer.positive", "Positive prompts")}
				autosize={true}
				minRows={3}
				maxRows={8}
				value={positive}
				onChange={(event) => onDraftChange({ ...draft, positive: event.currentTarget.value })}
			/>
			<Textarea
				label={t("pages.modelLab.steer.negative", "Negative prompts")}
				autosize={true}
				minRows={3}
				maxRows={8}
				value={negative}
				onChange={(event) => onDraftChange({ ...draft, negative: event.currentTarget.value })}
			/>
			<Textarea
				label={t("pages.modelLab.steer.tests", "Test prompts")}
				autosize={true}
				minRows={3}
				maxRows={8}
				value={tests}
				onChange={(event) => onDraftChange({ ...draft, tests: event.currentTarget.value })}
			/>
			<Group grow={true} align="flex-start">
				<NumberInput
					label={t("pages.modelLab.steer.layer", "Layer")}
					description={t("pages.modelLab.steer.layerHint", "Empty: a full-attention block at about 0.6 depth.")}
					placeholder={defaultLayer === null || defaultLayer === undefined ? undefined : String(defaultLayer)}
					allowDecimal={false}
					allowNegative={false}
					value={layer}
					onChange={(value) => onDraftChange({ ...draft, layer: value })}
				/>
				<NumberInput
					label={t("pages.modelLab.steer.scale", "Scale")}
					description={t("pages.modelLab.steer.scaleHint", "1 is half the residual's length; negative pushes the other way.")}
					decimalScale={3}
					step={0.5}
					value={scale}
					onChange={(value) => onDraftChange({ ...draft, scale: value })}
				/>
			</Group>
			{chosenLayer !== undefined && (
				<Text size="xs" c="dimmed" data-testid="model-lab-steering-layer-kind">
					{t("pages.modelLab.steer.layerKind", "Layer {{layer}} of {{last}}: {{kind}}.", {
						layer: chosenLayer,
						last: (steerableBlockCount ?? 1) - 1,
						kind: layerKindLabel(t, layerKindOf(layerTypes, chosenLayer)),
					})}
				</Text>
			)}
			{problem && (
				<Text size="sm" c="red" data-testid="model-lab-steering-problem">
					{problem}
				</Text>
			)}
			<Group>
				<Button
					disabled={!ready}
					loading={loading}
					onClick={() => {
						if (firstTest !== undefined && typeof scale === "number") {
							onStart({
								prompt: firstTest,
								steering: {
									positivePrompts: positiveLines,
									negativePrompts: negativeLines,
									additionalTestPrompts: moreTests,
									layer: typeof layer === "number" ? layer : null,
									scale,
								},
							});
						}
					}}
					data-testid="model-lab-steering-start"
				>
					{t("pages.modelLab.steer.start", "Start steering run")}
				</Button>
			</Group>
		</Stack>
	);
}
