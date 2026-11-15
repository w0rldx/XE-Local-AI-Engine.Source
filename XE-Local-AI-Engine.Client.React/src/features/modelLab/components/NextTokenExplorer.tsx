import {
	Alert,
	Button,
	Group,
	type MantineColor,
	Paper,
	ScrollArea,
	Select,
	Stack,
	Switch,
	Text,
	Textarea,
	Title,
} from "@mantine/core";
import { type KeyboardEvent, memo, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { runDirectInference } from "@/core/directInference/DirectInferenceApi";
import {
	type DirectInferenceLogprobEntry,
	type DirectInferenceResult,
	directInferenceMaxKeptLogprobEntries,
	directInferenceMaxTopLogprobs,
	logprobTextLabel,
} from "@/core/directInference/DirectInferenceModels";
import { DirectInferenceErrorAlert, DirectInferenceOutput } from "@/core/directInference/DirectInferenceOutput";
import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import { useDirectInferenceModels } from "@/core/directInference/useDirectInferenceModels";
import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { ExplanationLevelSwitch, ExplanationList } from "@/features/modelLab/components/ArchitectureExplanation";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { type ExplanationLevel, nextTokenTopics } from "@/features/modelLab/models/ModelLabExplanation";

interface Step {
	entry: DirectInferenceLogprobEntry;
	// The position in result.logprobs.
	index: number;
	// 1-based among the kept entries of the same kind.
	number: number;
}

interface StepButtonsProps {
	label: string;
	steps: readonly Step[];
	selected: number | null;
	onSelect: (index: number) => void;
	color: MantineColor;
}

// Native buttons, so Enter and Space select; the arrow keys move between them instead of tabbing through every step.
// Memoized: every streamed event re-renders the tab, and up to a thousand buttons only change when an entry was kept.
const StepButtons = memo(function StepButtons({ label, steps, selected, onSelect, color }: StepButtonsProps) {
	const moveFocus = (event: KeyboardEvent<HTMLDivElement>): void => {
		const buttons = [...event.currentTarget.querySelectorAll("button")];
		const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
		const next = { ArrowRight: at + 1, ArrowDown: at + 1, ArrowLeft: at - 1, ArrowUp: at - 1, Home: 0, End: buttons.length - 1 }[
			event.key
		];
		if (at !== -1 && next !== undefined && buttons[next]) {
			event.preventDefault();
			buttons[next].focus();
		}
	};
	return (
		<Stack gap={4}>
			<Text size="xs" fw={600}>
				{label}
			</Text>
			<Group gap={4} role="group" aria-label={label} onKeyDown={moveFocus}>
				{steps.map(({ entry, index }) => (
					<Button
						key={index}
						size="compact-xs"
						ff="monospace"
						color={color}
						variant={index === selected ? "filled" : "default"}
						aria-pressed={index === selected}
						onClick={() => onSelect(index)}
						styles={{ label: { whiteSpace: "pre" } }}
						data-testid="model-lab-next-token-step-button"
					>
						{logprobTextLabel(entry.text)}
					</Button>
				))}
			</Group>
		</Stack>
	);
});

// The stream only appends and keeps the same array when it kept nothing, so this splits only when an entry arrived.
function splitSteps(logprobs: readonly DirectInferenceLogprobEntry[] | undefined): { thinking: Step[]; answer: Step[] } {
	const thinking: Step[] = [];
	const answer: Step[] = [];
	for (const [index, entry] of (logprobs ?? []).entries()) {
		const kind = entry.isReasoning ? thinking : answer;
		kind.push({ entry, index, number: kind.length + 1 });
	}
	return { thinking, answer };
}

// One prompt on one installed llama.cpp model with the top candidates of every step. Held in this component only: nothing is saved.
export function NextTokenExplorer() {
	const { t } = useTranslation();
	const { modelGroups, llamaCppModels, status } = useDirectInferenceModels();
	const modelItems = modelGroups.flatMap((group) => group.items).filter((item) => llamaCppModels.has(item.value));
	const [model, setModel] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [result, setResult] = useState<DirectInferenceResult | undefined>();
	const [running, setRunning] = useState(false);
	const [showThinking, setShowThinking] = useState(false);
	// A position in result.logprobs: the stream only appends, so it stays the same entry.
	const [selected, setSelected] = useState<number | null>(null);
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const [openTopic, setOpenTopic] = useState<string | null>(null);
	const abortRef = useRef<AbortController | undefined>(undefined);
	// Leaving mid-run cancels it, or it keeps holding the node's invocation slot.
	useEffect(() => () => abortRef.current?.abort(), []);

	const run = async (): Promise<void> => {
		if (model === null || prompt.trim().length === 0) {
			return;
		}
		const controller = new AbortController();
		abortRef.current = controller;
		setSelected(null);
		setRunning(true);
		await runDirectInference(
			{
				modelId: model,
				prompt,
				systemPrompt: systemPrompt.trim().length > 0 ? systemPrompt : undefined,
				samplingOptions: toWireSamplingOptions(sampling),
				topLogprobs: directInferenceMaxTopLogprobs,
			},
			setResult,
			controller.signal,
		);
		// A stopped attempt already went idle, and by now the panel may belong to a newer run.
		if (!controller.signal.aborted) {
			setRunning(false);
			abortRef.current = undefined;
		}
	};

	// Idle at once: while the hub is still connecting, the attempt only settles when the connection does.
	const stop = (): void => {
		abortRef.current?.abort();
		setRunning(false);
	};

	const logprobs = result?.logprobs;
	const { thinking: thinkingSteps, answer: answerSteps } = useMemo(() => splitSteps(logprobs), [logprobs]);
	const visibleSteps = showThinking ? [...thinkingSteps, ...answerSteps] : answerSteps;
	const selectedStep = visibleSteps.find((step) => step.index === selected);
	const thinkingOnly = !showThinking && answerSteps.length === 0 && thinkingSteps.length > 0;
	const topicLabels: Record<(typeof nextTokenTopics)[number]["id"], string> = {
		logits: t("pages.modelLab.nextToken.ui.topic.logits", "Logits"),
		softmax: t("pages.modelLab.nextToken.ui.topic.softmax", "Softmax"),
		logprob: t("pages.modelLab.nextToken.ui.topic.logprob", "Log-probability"),
		temperature: t("pages.modelLab.nextToken.ui.topic.temperature", "Temperature"),
		topK: t("pages.modelLab.nextToken.ui.topic.topK", "Top-k"),
		topP: t("pages.modelLab.nextToken.ui.topic.topP", "Top-p"),
		minP: t("pages.modelLab.nextToken.ui.topic.minP", "Min-p"),
		greedy: t("pages.modelLab.nextToken.ui.topic.greedy", "Greedy or sampled"),
		entries: t("pages.modelLab.nextToken.ui.topic.entries", "Why steps are not tokens"),
	};
	const reasoningLeftOut = result?.reasoningLogprobsLeftOut ?? 0;
	const answerLeftOut = result?.answerLogprobsLeftOut ?? 0;

	return (
		<Stack gap="md">
			<Alert color="blue" data-testid="model-lab-next-token-honesty">
				<Text size="sm">
					{t(
						"pages.modelLab.nextToken.ui.preSampling",
						"The bars show the probabilities llama.cpp computed before sampling: the run's temperature, top-k and top-p do not change them.",
					)}
				</Text>
				<Text size="sm">
					{t(
						"pages.modelLab.nextToken.ui.notTokens",
						"Steps are entries, not tokens: multi-byte text can merge several tokens into one entry, whose values belong to its last token.",
					)}
				</Text>
				<Text size="sm">
					{t(
						"pages.modelLab.nextToken.ui.lastMissing",
						"The stream leaves out the last step, and with thinking on some newline steps, so the steps can differ from the text.",
					)}
				</Text>
			</Alert>
			<ModelPickerNotice status={status} modelCount={modelItems.length} />
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<Select
						label={t("pages.modelLab.nextToken.ui.model", "Model")}
						placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
						data={modelItems}
						value={model}
						onChange={setModel}
						searchable={true}
						disabled={running}
					/>
					<Textarea
						label={t("pages.directInference.systemPrompt", "System prompt (optional)")}
						autosize={true}
						minRows={2}
						maxRows={10}
						value={systemPrompt}
						onChange={(event) => setSystemPrompt(event.currentTarget.value)}
						disabled={running}
					/>
					<Textarea
						label={t("pages.directInference.prompt", "Prompt")}
						autosize={true}
						minRows={4}
						maxRows={20}
						value={prompt}
						onChange={(event) => setPrompt(event.currentTarget.value)}
						disabled={running}
					/>
					<DirectInferenceSamplingFields value={sampling} onChange={setSampling} disabled={running} />
					<Group>
						<Button onClick={run} loading={running} disabled={model === null || prompt.trim().length === 0}>
							{t("pages.modelLab.nextToken.ui.run", "Run")}
						</Button>
						{running && (
							<Button variant="default" onClick={stop}>
								{t("pages.directInference.stop", "Stop")}
							</Button>
						)}
					</Group>
				</Stack>
			</Paper>
			{result && (
				<>
					<DirectInferenceErrorAlert result={result} />
					<DirectInferenceOutput result={result} />
					<Paper withBorder={true} p="md">
						<Stack gap="sm" data-testid="model-lab-next-token-steps">
							<Group justify="space-between">
								<Title order={5}>{t("pages.modelLab.nextToken.ui.stepsTitle", "Steps")}</Title>
								<Switch
									label={t("pages.modelLab.nextToken.ui.showThinking", "Show thinking steps")}
									checked={showThinking}
									onChange={(event) => setShowThinking(event.currentTarget.checked)}
								/>
							</Group>
							{(logprobs?.length ?? 0) === 0 && result.elapsedMs !== undefined && (
								<Text size="sm" c="dimmed" data-testid="model-lab-next-token-none">
									{t("pages.directInference.logprobs.none", "The server returned no probabilities for this request.")}
								</Text>
							)}
							<ScrollArea.Autosize mah={240}>
								<Stack gap="xs">
									{showThinking && thinkingSteps.length > 0 && (
										<StepButtons
											label={t("pages.directInference.logprobs.thinking", "Thinking")}
											steps={thinkingSteps}
											selected={selected}
											onSelect={setSelected}
											color="gray"
										/>
									)}
									{answerSteps.length > 0 && (
										<StepButtons
											label={t("pages.modelLab.nextToken.ui.answer", "Answer")}
											steps={answerSteps}
											selected={selected}
											onSelect={setSelected}
											color="blue"
										/>
									)}
								</Stack>
							</ScrollArea.Autosize>
							{thinkingOnly && (
								<Text size="sm" c="dimmed" data-testid="model-lab-next-token-thinking-only">
									{running
										? t(
												"pages.modelLab.nextToken.ui.thinkingOnly",
												"So far only thinking steps arrived. Show them with the switch above.",
											)
										: result.finishReason === "length"
											? t(
													"pages.modelLab.nextToken.ui.thinkingOnlyFinished",
													"This run produced only thinking steps; the answer had not started when the output limit was reached. Show them with the switch above, or raise Max output tokens.",
												)
											: t(
													"pages.modelLab.nextToken.ui.thinkingOnlyNoAnswer",
													"This run produced only thinking steps; the answer, if any, was too short to get a step, since the stream leaves out the final entry. Show the thinking steps with the switch above.",
												)}
								</Text>
							)}
							{reasoningLeftOut > 0 && (
								<Text size="xs" c="dimmed" data-testid="model-lab-next-token-left-out-reasoning">
									{t(
										"pages.directInference.logprobs.leftOutReasoning",
										"Thinking: showing the first {{kept}} entries; {{leftOut}} more were left out.",
										{ kept: directInferenceMaxKeptLogprobEntries, leftOut: reasoningLeftOut },
									)}
								</Text>
							)}
							{answerLeftOut > 0 && (
								<Text size="xs" c="dimmed" data-testid="model-lab-next-token-left-out-answer">
									{t(
										"pages.directInference.logprobs.leftOutAnswer",
										"Answer: showing the first {{kept}} entries; {{leftOut}} more were left out.",
										{ kept: directInferenceMaxKeptLogprobEntries, leftOut: answerLeftOut },
									)}
								</Text>
							)}
							{selectedStep ? (
								<NextTokenStepView entry={selectedStep.entry} stepNumber={selectedStep.number} />
							) : (
								visibleSteps.length > 0 && (
									<Text size="sm" c="dimmed">
										{t("pages.modelLab.nextToken.ui.pickStep", "Pick a step to see its candidates.")}
									</Text>
								)
							)}
						</Stack>
					</Paper>
				</>
			)}
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<ExplanationLevelSwitch value={level} onChange={setLevel} />
					<ExplanationList
						items={nextTokenTopics.map((topic) => ({ ...topic, label: topicLabels[topic.id], candidates: [topic.id] }))}
						level={level}
						basis="background"
						root="pages.modelLab.nextToken.explain"
						open={openTopic}
						onOpen={setOpenTopic}
						data-testid="model-lab-next-token-explain"
					/>
				</Stack>
			</Paper>
		</Stack>
	);
}
