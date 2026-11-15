import { Alert, Badge, Group, Paper, Progress, SimpleGrid, Slider, Stack, Text, Title } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { logprobPercentLabel, logprobTextLabel } from "@/core/directInference/DirectInferenceModels";
import {
	neutralSamplerSettings,
	type NextTokenStepEntry,
	type SamplerSettings,
	simulateSampling,
} from "@/features/modelLab/models/NextTokenSampling";

interface CandidateBarProps {
	text?: string | null;
	chosen: boolean;
	// 0 to 1; null marks a candidate the simulation cut.
	probability: number | null;
	color: string;
}

function CandidateBar({ text, chosen, probability, color }: CandidateBarProps) {
	const { t } = useTranslation();
	return (
		<Stack gap={2} data-testid="model-lab-next-token-bar">
			<Group gap={6} justify="space-between" wrap="nowrap">
				<Group gap={6} wrap="nowrap" miw={0}>
					<Text size="sm" ff="monospace" style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>
						{logprobTextLabel(text)}
					</Text>
					{chosen && (
						<Badge size="xs" variant="filled" data-testid="model-lab-next-token-chosen">
							{t("pages.modelLab.nextToken.ui.chosen", "Chosen")}
						</Badge>
					)}
				</Group>
				<Text size="sm" c={probability === null ? "dimmed" : undefined} style={{ flexShrink: 0 }}>
					{probability === null ? t("pages.modelLab.nextToken.ui.cut", "Cut") : logprobPercentLabel(Math.log(probability))}
				</Text>
			</Group>
			{/* The percentage beside it carries the value; the bar repeats it for the eye. */}
			<Progress value={(probability ?? 0) * 100} color={chosen ? "teal" : color} size="sm" aria-hidden={true} />
		</Stack>
	);
}

interface NextTokenStepViewProps {
	entry: NextTokenStepEntry;
	// 1-based among the kept entries of the same kind, thinking or answer.
	stepNumber: number;
	// Set for a PyTorch trace's exact candidates: `basis` titles the measured column, and the llama-server wording gives
	// way to the trace's. `processed` when the list is the processed scores, from which generation picked.
	trace?: { basis: string; processed: boolean };
}

// One step's candidates as the server measured them, and beside them a sampler simulation over the same candidates.
export function NextTokenStepView({ entry, stepNumber, trace }: NextTokenStepViewProps) {
	const { t } = useTranslation();
	const [settings, setSettings] = useState<SamplerSettings>(neutralSamplerSettings);
	const simulated = simulateSampling(entry.alternatives, settings);
	const shownProbability = entry.alternatives.reduce((sum, alternative) => sum + Math.exp(alternative.logprob), 0);
	// By id when known; else only the first candidate with the chosen text is marked, since entry texts can repeat.
	const chosenIndex = simulated.findIndex((candidate) =>
		entry.id === undefined ? candidate.text === entry.text : candidate.id === entry.id,
	);
	const count = entry.alternatives.length;
	const fromTrace = trace !== undefined;
	const titleValues = {
		step: stepNumber,
		text: logprobTextLabel(entry.text),
		percent: logprobPercentLabel(entry.logprob),
	};

	return (
		<Stack gap="sm" data-testid="model-lab-next-token-step">
			<Title order={5} style={{ overflowWrap: "anywhere" }}>
				{entry.isReasoning
					? t("pages.modelLab.nextToken.ui.stepTitleThinking", "Thinking step {{step}}: {{text}} ({{percent}})", titleValues)
					: t("pages.modelLab.nextToken.ui.stepTitleAnswer", "Answer step {{step}}: {{text}} ({{percent}})", titleValues)}
			</Title>
			{chosenIndex === -1 && (
				<Alert color="yellow" data-testid="model-lab-next-token-chosen-missing">
					{trace?.processed
						? t(
								"pages.modelLab.nextToken.ui.chosenMissingProcessed",
								"The chosen token is not among these {{count}} candidates.",
								{
									count,
								},
							)
						: trace
							? t(
									"pages.modelLab.nextToken.ui.chosenMissingTrace",
									"The chosen token is not among these {{count}} candidates: generation picked it from the processed scores, where it ranked higher.",
									{ count },
								)
							: t(
									"pages.modelLab.nextToken.ui.chosenMissing",
									"The chosen entry's text matches none of the {{count}} candidates: the model drew something outside them, or this entry merges several tokens (see “Why steps are not tokens”).",
									{ count },
								)}
				</Alert>
			)}
			<SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
				<Paper withBorder={true} p="md">
					<Stack gap="xs" data-testid="model-lab-next-token-measured">
						<Text size="sm" fw={600}>
							{trace?.basis ?? t("pages.modelLab.nextToken.ui.measuredTitle", "Measured by llama.cpp")}
						</Text>
						{entry.alternatives.length === 0 && (
							<Text size="sm" c="dimmed">
								{fromTrace
									? t("pages.modelLab.nextToken.ui.noCandidatesTrace", "This trace recorded no candidates for this step.")
									: t("pages.modelLab.nextToken.ui.noCandidates", "The server returned no candidates for this step.")}
							</Text>
						)}
						{simulated.map((candidate, index) => (
							<CandidateBar
								// The list is sorted once per render and only its position identifies a candidate: texts can repeat.
								// biome-ignore lint/suspicious/noArrayIndexKey: see above.
								key={index}
								text={candidate.text}
								chosen={index === chosenIndex}
								probability={Math.exp(candidate.logprob)}
								color="blue"
							/>
						))}
						<Text size="xs" c="dimmed" data-testid="model-lab-next-token-rest">
							{t("pages.modelLab.nextToken.ui.rest", "The rest of the vocabulary: {{percent}}", {
								percent: logprobPercentLabel(Math.log(Math.max(1 - shownProbability, 0))),
							})}
						</Text>
					</Stack>
				</Paper>
				<Paper withBorder={true} p="md">
					<Stack gap="xs" data-testid="model-lab-next-token-simulated">
						<Text size="sm" fw={600}>
							{t("pages.modelLab.nextToken.ui.simulatedTitle", "Simulated sampler")}
						</Text>
						<Alert color="grape" p="xs" data-testid="model-lab-next-token-sim-label">
							<Text size="xs">
								{fromTrace
									? t(
											"pages.modelLab.nextToken.ui.simulatedLabelTrace",
											"Simulated over the {{count}} shown candidates, renormalized; not what generation picked.",
											{ count },
										)
									: t(
											"pages.modelLab.nextToken.ui.simulatedLabel",
											"Simulated over the {{count}} shown candidates, renormalized; not what the server sampled.",
											{ count },
										)}
							</Text>
							<Text size="xs">
								{t(
									"pages.modelLab.nextToken.ui.simulatedOrder",
									"Applied in llama.cpp's default order: top-k, then top-p, then min-p, then temperature. Penalties and llama.cpp's other samplers are not simulated.",
								)}
							</Text>
							{!fromTrace && (
								<Text size="xs" data-testid="model-lab-next-token-server-defaults">
									{t(
										"pages.modelLab.nextToken.ui.serverDefaults",
										"Sampling values a run leaves empty use llama-server's defaults (top-k 40, top-p 0.95, min-p 0.05, temperature 0.8) unless the model file or its launch arguments set others. Top-k 40 is more than the shown candidates, so the simulation may then differ from the server.",
									)}
								</Text>
							)}
						</Alert>
						<Stack gap={4}>
							<Text size="xs">
								{t("pages.modelLab.nextToken.ui.temperature", "Temperature: {{value}}", {
									value: settings.temperature.toFixed(2),
								})}
							</Text>
							<Slider
								min={0}
								max={2}
								step={0.05}
								value={settings.temperature}
								onChange={(temperature) => setSettings({ ...settings, temperature })}
								thumbLabel={t("pages.modelLab.nextToken.ui.temperatureLabel", "Simulated temperature")}
								label={null}
								data-testid="model-lab-next-token-temperature"
							/>
						</Stack>
						<Stack gap={4}>
							<Text size="xs">
								{settings.topK > 0
									? t("pages.modelLab.nextToken.ui.topK", "Top-k: {{value}}", { value: settings.topK })
									: t("pages.modelLab.nextToken.ui.topKOff", "Top-k: off")}
							</Text>
							<Slider
								min={0}
								max={20}
								step={1}
								value={settings.topK}
								onChange={(topK) => setSettings({ ...settings, topK })}
								thumbLabel={t("pages.modelLab.nextToken.ui.topKLabel", "Simulated top-k")}
								label={null}
								data-testid="model-lab-next-token-top-k"
							/>
						</Stack>
						<Stack gap={4}>
							<Text size="xs">
								{settings.topP < 1
									? t("pages.modelLab.nextToken.ui.topP", "Top-p: {{value}}", { value: settings.topP.toFixed(2) })
									: t("pages.modelLab.nextToken.ui.topPOff", "Top-p: off")}
							</Text>
							<Slider
								min={0}
								max={1}
								step={0.05}
								value={settings.topP}
								onChange={(topP) => setSettings({ ...settings, topP })}
								thumbLabel={t("pages.modelLab.nextToken.ui.topPLabel", "Simulated top-p")}
								label={null}
								data-testid="model-lab-next-token-top-p"
							/>
						</Stack>
						<Stack gap={4}>
							<Text size="xs">
								{settings.minP > 0
									? t("pages.modelLab.nextToken.ui.minP", "Min-p: {{value}}", { value: settings.minP.toFixed(2) })
									: t("pages.modelLab.nextToken.ui.minPOff", "Min-p: off")}
							</Text>
							<Slider
								min={0}
								max={1}
								step={0.01}
								value={settings.minP}
								onChange={(minP) => setSettings({ ...settings, minP })}
								thumbLabel={t("pages.modelLab.nextToken.ui.minPLabel", "Simulated min-p")}
								label={null}
								data-testid="model-lab-next-token-min-p"
							/>
						</Stack>
						{simulated.map((candidate, index) => (
							<CandidateBar
								// biome-ignore lint/suspicious/noArrayIndexKey: the same sorted list as the measured column.
								key={index}
								text={candidate.text}
								chosen={index === chosenIndex}
								probability={candidate.kept ? candidate.probability : null}
								color="grape"
							/>
						))}
					</Stack>
				</Paper>
			</SimpleGrid>
		</Stack>
	);
}
