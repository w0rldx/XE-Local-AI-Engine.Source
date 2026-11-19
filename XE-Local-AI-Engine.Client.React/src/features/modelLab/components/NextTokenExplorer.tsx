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
import { useMutation } from "@tanstack/react-query";
import { memo, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { tokenizeModelLabPromptMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { runDirectInference } from "@/core/directInference/DirectInferenceApi";
import {
	type DirectInferenceLogprobEntry,
	type DirectInferenceResult,
	directInferenceMaxKeptLogprobEntries,
	directInferenceMaxTopLogprobs,
	emptyDirectInferenceResult,
	logprobTextLabel,
} from "@/core/directInference/DirectInferenceModels";
import { DirectInferenceErrorAlert, DirectInferenceOutput } from "@/core/directInference/DirectInferenceOutput";
import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import { DirectInferenceThinkingSwitch } from "@/core/directInference/DirectInferenceThinkingSwitch";
import { useAbortableRun } from "@/core/directInference/useAbortableRun";
import { useDirectInferenceModels } from "@/core/directInference/useDirectInferenceModels";
import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { useStreamCommitScheduler } from "@/core/streaming/useStreamCommitScheduler";
import { ExplanationLevelSwitch, ExplanationList } from "@/features/modelLab/components/ArchitectureExplanation";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { NextTokenPromptTokens } from "@/features/modelLab/components/NextTokenPromptTokens";
import { NextTokenStepView } from "@/features/modelLab/components/NextTokenStepView";
import { type ExplanationLevel, nextTokenTopics } from "@/features/modelLab/models/ModelLabExplanation";
import { moveFocusAmongButtons } from "@/features/modelLab/models/ModelLabFocus";
import type { ModelLabIntent } from "@/features/modelLab/models/ModelLabNavigation";

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
// Memoized: every streamed frame re-renders the tab, and up to a thousand buttons only change when an entry was kept.
const StepButtons = memo(function StepButtons({ label, steps, selected, onSelect, color }: StepButtonsProps) {
	return (
		<Stack gap={4}>
			<Text size="xs" fw={600}>
				{label}
			</Text>
			<Group gap={4} role="group" aria-label={label} onKeyDown={moveFocusAmongButtons}>
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
// An intent from another tab prefills the form once; edits after it are the user's.
export function NextTokenExplorer({ intent }: { intent?: ModelLabIntent<"nextToken"> }) {
	const { t } = useTranslation();
	const { modelGroups, llamaCppModels, reasoningLlamaCppModels, status } = useDirectInferenceModels();
	const modelItems = modelGroups.flatMap((group) => group.items).filter((item) => llamaCppModels.has(item.value));
	const [model, setModel] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [disableThinking, setDisableThinking] = useState(false);
	const [result, setResult] = useState<DirectInferenceResult | undefined>();
	const { running, start, stop } = useAbortableRun();
	const resultCommits = useStreamCommitScheduler<DirectInferenceResult>(setResult);
	const [showThinking, setShowThinking] = useState(false);
	// A position in result.logprobs: the stream only appends, so it stays the same entry.
	const [selected, setSelected] = useState<number | null>(null);
	const [level, setLevel] = useState<ExplanationLevel>("overview");
	const [openTopic, setOpenTopic] = useState<string | null>(null);
	// Set by Try in Next token on a trace: the form holds that trace's prompt and paired GGUF.
	const [fromTrace, setFromTrace] = useState(false);
	const [appliedNonce, setAppliedNonce] = useState<number>();
	if (intent && intent.nonce !== appliedNonce) {
		setAppliedNonce(intent.nonce);
		setModel(intent.modelName);
		setSystemPrompt("");
		setPrompt(intent.prompt);
		setDisableThinking(intent.disableThinking);
		setFromTrace(true);
	}
	// Read once per run, after it ends; never on a render.
	const promptTokens = useMutation(tokenizeModelLabPromptMutation());

	const isReasoningModel = model !== null && reasoningLlamaCppModels.has(model);
	// Another model starts from its own default again.
	const selectModel = (next: string | null): void => {
		setModel(next);
		setDisableThinking(false);
		setFromTrace(false);
	};

	const run = async (): Promise<void> => {
		if (model === null || prompt.trim().length === 0) {
			return;
		}
		// A stopped run's frame that never painted must not merge into this one.
		resultCommits.cancel();
		setSelected(null);
		promptTokens.reset();
		const sentSystemPrompt = systemPrompt.trim().length > 0 ? systemPrompt : undefined;
		const sentDisableThinking = isReasoningModel && disableThinking;
		await start(async (signal, isCurrent) => {
			let last = emptyDirectInferenceResult;
			await runDirectInference(
				{
					modelId: model,
					prompt,
					systemPrompt: sentSystemPrompt,
					samplingOptions: toWireSamplingOptions(sampling),
					topLogprobs: directInferenceMaxTopLogprobs,
					disableThinking: sentDisableThinking ? true : undefined,
				},
				(next) => {
					last = next;
					resultCommits.schedule(next);
				},
				signal,
			);
			// A finished or stopped run shows its last events at once, not on the next frame.
			resultCommits.flush();
			// The tokens of exactly what this run sent, once it completed or was stopped with output. Not after a failure,
			// not for a run a newer one replaced, and not for a page that is gone.
			const failed = last.errorCode !== undefined || last.errorMessage !== undefined;
			const hasOutput = last.text !== "" || last.reasoning !== "";
			if (isCurrent() && !failed && (!signal.aborted || hasOutput)) {
				promptTokens.mutate({
					body: { modelName: model, systemPrompt: sentSystemPrompt, prompt, disableThinking: sentDisableThinking },
				});
			}
		});
	};

	const logprobs = result?.logprobs;
	const { thinking: thinkingSteps, answer: answerSteps } = useMemo(() => splitSteps(logprobs), [logprobs]);
	const visibleSteps = showThinking ? [...thinkingSteps, ...answerSteps] : answerSteps;
	const selectedStep = visibleSteps.find((step) => step.index === selected);
	const thinkingOnly = !showThinking && answerSteps.length === 0 && thinkingSteps.length > 0;
	const topicLabels: Record<(typeof nextTokenTopics)[number]["id"], string> = {
		promptTokens: t("pages.modelLab.nextToken.ui.topic.promptTokens", "Prompt tokens"),
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
					{fromTrace && (
						<Text size="sm" c="dimmed" data-testid="model-lab-next-token-from-trace">
							{t(
								"pages.modelLab.nextToken.ui.fromTrace",
								"Prefilled from a trace. Next token runs the paired GGUF with llama.cpp, so its tokens and probabilities can differ from the trace, which ran the Hugging Face checkpoint in PyTorch.",
							)}
						</Text>
					)}
					<Select
						label={t("pages.modelLab.nextToken.ui.model", "Model")}
						placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
						data={modelItems}
						value={model}
						onChange={selectModel}
						searchable={true}
						disabled={running}
					/>
					<Textarea
						label={t("pages.directInference.systemPrompt", "System prompt (optional)")}
						autosize={true}
						minRows={2}
						maxRows={10}
						value={systemPrompt}
						onChange={(event) => {
							setSystemPrompt(event.currentTarget.value);
							setFromTrace(false);
						}}
						disabled={running}
					/>
					<Textarea
						label={t("pages.directInference.prompt", "Prompt")}
						autosize={true}
						minRows={4}
						maxRows={20}
						value={prompt}
						onChange={(event) => {
							setPrompt(event.currentTarget.value);
							// The form no longer holds the trace's input, so the note about it no longer applies.
							setFromTrace(false);
						}}
						disabled={running}
					/>
					{isReasoningModel && (
						<DirectInferenceThinkingSwitch checked={disableThinking} onChange={setDisableThinking} disabled={running} />
					)}
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
			{!promptTokens.isIdle && (
				<NextTokenPromptTokens pending={promptTokens.isPending} data={promptTokens.data} error={promptTokens.error} />
			)}
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
