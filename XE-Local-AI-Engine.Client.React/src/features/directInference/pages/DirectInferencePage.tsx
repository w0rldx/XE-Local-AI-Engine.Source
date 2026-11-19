import { Button, Group, NumberInput, Paper, Select, Stack, Textarea, Title } from "@mantine/core";
import { useClipboard } from "@mantine/hooks";
import { IconBolt } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { runDirectInference } from "@/core/directInference/DirectInferenceApi";
import {
	type DirectInferenceRequest,
	type DirectInferenceResult,
	directInferenceDefaultMaxOutputTokens,
	directInferenceMaxTopLogprobs,
	emptyDirectInferenceResult,
} from "@/core/directInference/DirectInferenceModels";
import { DirectInferenceErrorAlert, DirectInferenceOutput } from "@/core/directInference/DirectInferenceOutput";
import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import { DirectInferenceThinkingSwitch } from "@/core/directInference/DirectInferenceThinkingSwitch";
import { useAbortableRun } from "@/core/directInference/useAbortableRun";
import { useDirectInferenceModels } from "@/core/directInference/useDirectInferenceModels";
import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { useStreamCommitScheduler } from "@/core/streaming/useStreamCommitScheduler";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { StickyBar } from "@/core/ui/components/StickyBar/StickyBar";
import { DirectInferenceLogprobs } from "@/features/directInference/components/DirectInferenceLogprobs";

/** One explicit prompt to one model: no agent, tools, history, persona, memory or knowledge base, and nothing saved. */
export function DirectInferencePage() {
	const { t } = useTranslation();
	const { modelGroups, llamaCppModels, reasoningLlamaCppModels } = useDirectInferenceModels();
	const [modelId, setModelId] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [topLogprobs, setTopLogprobs] = useState<number | undefined>();
	const [disableThinking, setDisableThinking] = useState(false);
	const [result, setResult] = useState<DirectInferenceResult>(emptyDirectInferenceResult);
	const [lastRequest, setLastRequest] = useState<DirectInferenceRequest | undefined>();
	const { running, start, stop } = useAbortableRun();
	const resultCommits = useStreamCommitScheduler(setResult);
	const clipboard = useClipboard();

	const isLlamaCppModel = modelId !== null && llamaCppModels.has(modelId);
	const isReasoningLlamaCppModel = modelId !== null && reasoningLlamaCppModels.has(modelId);
	// Another model starts from its own default again.
	const selectModel = (next: string | null): void => {
		setModelId(next);
		setDisableThinking(false);
	};

	const run = async (): Promise<void> => {
		if (modelId === null || prompt.trim().length === 0) {
			return;
		}
		const request: DirectInferenceRequest = {
			modelId,
			prompt,
			systemPrompt: systemPrompt.trim().length > 0 ? systemPrompt : undefined,
			samplingOptions: toWireSamplingOptions(sampling),
			topLogprobs: isLlamaCppModel ? topLogprobs : undefined,
			disableThinking: isReasoningLlamaCppModel && disableThinking ? true : undefined,
		};
		// A stopped run's frame that never painted must not merge into this one.
		resultCommits.cancel();
		setLastRequest(request);
		await start(async (signal) => {
			await runDirectInference(request, resultCommits.schedule, signal);
			// A finished or stopped run shows its last events at once, not on the next frame.
			resultCommits.flush();
		});
	};

	return (
		<PageShell>
			<PageHeader
				icon={<IconBolt size={24} />}
				title={t("pages.directInference.title", "Direct inference")}
				subtitle={t(
					"pages.directInference.subtitle",
					"Send one prompt straight to a model: no agent, tools, history, memory or knowledge base. Nothing is saved.",
				)}
			/>
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<Select
						label={t("pages.directInference.model", "Model")}
						placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
						data={modelGroups}
						value={modelId}
						onChange={selectModel}
						searchable={true}
						disabled={running}
						data-testid="direct-inference-model"
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
					{isReasoningLlamaCppModel && (
						<DirectInferenceThinkingSwitch checked={disableThinking} onChange={setDisableThinking} disabled={running} />
					)}
					<DirectInferenceSamplingFields
						value={sampling}
						onChange={setSampling}
						disabled={running}
						defaultMaxOutputTokens={isLlamaCppModel ? directInferenceDefaultMaxOutputTokens : undefined}
					/>
					{isLlamaCppModel && (
						<NumberInput
							label={t("pages.directInference.logprobs.field", "Token probabilities")}
							description={t(
								"pages.directInference.logprobs.fieldDescription",
								"Alternatives to show per entry, 1 to {{max}}. Leave empty for none. For installed llama.cpp models only.",
								{ max: directInferenceMaxTopLogprobs },
							)}
							min={1}
							max={directInferenceMaxTopLogprobs}
							allowDecimal={false}
							clampBehavior="strict"
							disabled={running}
							inputWrapperOrder={["label", "input", "description", "error"]}
							value={topLogprobs ?? ""}
							onChange={(next) => setTopLogprobs(typeof next === "number" ? next : undefined)}
						/>
					)}
					<StickyBar edge="bottom" data-testid="direct-inference-actions">
						<Group>
							<Button onClick={run} loading={running} disabled={modelId === null || prompt.trim().length === 0}>
								{t("pages.directInference.send", "Send")}
							</Button>
							{running && (
								<Button variant="default" onClick={stop}>
									{t("pages.directInference.stop", "Stop")}
								</Button>
							)}
						</Group>
					</StickyBar>
				</Stack>
			</Paper>
			<DirectInferenceErrorAlert result={result} />
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<Group justify="space-between">
						<Title order={4}>{t("pages.directInference.output", "Output")}</Title>
						{/* Stringified on click: every streamed frame re-renders this page. */}
						<Button
							size="xs"
							variant="default"
							onClick={() => clipboard.copy(JSON.stringify({ request: lastRequest, result }, null, 2))}
							disabled={lastRequest === undefined}
						>
							{clipboard.copied
								? t("pages.directInference.copied", "Copied")
								: t("pages.directInference.copyJson", "Copy as JSON")}
						</Button>
					</Group>
					<DirectInferenceOutput result={result} />
					{lastRequest?.topLogprobs !== undefined && (!result.errorMessage || (result.logprobs?.length ?? 0) > 0) && (
						<DirectInferenceLogprobs result={result} running={running} />
					)}
				</Stack>
			</Paper>
		</PageShell>
	);
}
