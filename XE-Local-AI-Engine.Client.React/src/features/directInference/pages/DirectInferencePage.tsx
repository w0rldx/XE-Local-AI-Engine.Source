import { Button, Group, NumberInput, Paper, Select, Stack, Textarea, Title } from "@mantine/core";
import { useClipboard } from "@mantine/hooks";
import { IconBolt } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { runDirectInference } from "@/core/directInference/DirectInferenceApi";
import { DirectInferenceErrorAlert, DirectInferenceOutput } from "@/core/directInference/DirectInferenceOutput";
import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import { useDirectInferenceModels } from "@/core/directInference/useDirectInferenceModels";
import {
	type DirectInferenceRequest,
	type DirectInferenceResult,
	directInferenceDefaultMaxOutputTokens,
	directInferenceMaxTopLogprobs,
	emptyDirectInferenceResult,
} from "@/core/directInference/DirectInferenceModels";
import { DirectInferenceLogprobs } from "@/features/directInference/components/DirectInferenceLogprobs";

/** One explicit prompt to one model: no agent, tools, history, persona, memory or knowledge base, and nothing saved. */
export function DirectInferencePage() {
	const { t } = useTranslation();
	const { modelGroups, llamaCppModels } = useDirectInferenceModels();
	const [modelId, setModelId] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [topLogprobs, setTopLogprobs] = useState<number | undefined>();
	const [result, setResult] = useState<DirectInferenceResult>(emptyDirectInferenceResult);
	const [lastRequest, setLastRequest] = useState<DirectInferenceRequest | undefined>();
	const [running, setRunning] = useState(false);
	const clipboard = useClipboard();
	const abortRef = useRef<AbortController | undefined>(undefined);
	// Leaving the page mid-stream cancels the request, or it keeps holding the node's invocation slot.
	useEffect(() => () => abortRef.current?.abort(), []);

	const isLlamaCppModel = modelId !== null && llamaCppModels.has(modelId);

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
		};
		const controller = new AbortController();
		abortRef.current = controller;
		setLastRequest(request);
		setRunning(true);
		await runDirectInference(request, setResult, controller.signal);
		// A stopped attempt already went idle, and by now the page may belong to a newer Send.
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
						onChange={setModelId}
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
				</Stack>
			</Paper>
			<DirectInferenceErrorAlert result={result} />
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<Group justify="space-between">
						<Title order={4}>{t("pages.directInference.output", "Output")}</Title>
						{/* Stringified on click: every streamed event re-renders this page. */}
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
