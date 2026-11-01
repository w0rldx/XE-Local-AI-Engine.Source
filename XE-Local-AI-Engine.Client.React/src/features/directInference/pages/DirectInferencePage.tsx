import { Alert, Button, CopyButton, Group, Paper, Select, Stack, Text, Textarea, Title } from "@mantine/core";
import { IconBolt } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { toChatModelOptions, toExternalModelOptions } from "@/core/models/ChatModelOptions";
import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { streamDirectInference } from "@/features/directInference/api/DirectInferenceApi";
import { DirectInferenceMetrics } from "@/features/directInference/components/DirectInferenceMetrics";
import { DirectInferenceSamplingFields } from "@/features/directInference/components/DirectInferenceSamplingFields";
import {
	applyDirectInferenceEvent,
	type DirectInferenceRequest,
	type DirectInferenceResult,
	emptyDirectInferenceResult,
} from "@/features/directInference/models/DirectInferenceModels";

// The providers the unified models list tags cloud entries with (the chat picker reads the same two).
const cloudProviders = new Set(["CodexOAuth", "AzureFoundry"]);
const gpuWorkExclusiveCode = "GpuWorkExclusive";

interface ModelGroup {
	group: string;
	items: { value: string; label: string }[];
}

function useDirectInferenceModelGroups(): ModelGroup[] {
	const { t } = useTranslation();
	const { data } = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	return useMemo(() => {
		const items = data?.items ?? [];
		// Availability is not filtered here: the server refuses a model it cannot run with a classified error event.
		const toItems = (options: { value: string; label: string; displayName?: string }[]) =>
			options.map((option) => ({ value: option.value, label: option.displayName ?? option.label }));
		const groups: ModelGroup[] = [
			{
				group: t("pages.directInference.modelGroups.local", "Local"),
				items: toItems(toChatModelOptions(items, data?.isAvailable ?? false)),
			},
			{ group: t("pages.directInference.modelGroups.external", "External"), items: toItems(toExternalModelOptions(items, true)) },
			{
				group: t("pages.directInference.modelGroups.cloud", "Cloud"),
				items: items
					.filter((item) => cloudProviders.has(item.provider ?? "") && (item.modelName ?? "").length > 0)
					.map((item) => ({ value: item.modelName ?? "", label: item.displayLabel ?? item.modelName ?? "" })),
			},
		];
		return groups.filter((group) => group.items.length > 0);
	}, [data, t]);
}

/** One explicit prompt to one model: no agent, tools, history, persona, memory or knowledge base, and nothing saved. */
export function DirectInferencePage() {
	const { t } = useTranslation();
	const modelGroups = useDirectInferenceModelGroups();
	const [modelId, setModelId] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [result, setResult] = useState<DirectInferenceResult>(emptyDirectInferenceResult);
	const [lastRequest, setLastRequest] = useState<DirectInferenceRequest | undefined>();
	const [running, setRunning] = useState(false);
	const abortRef = useRef<AbortController | undefined>(undefined);
	// Leaving the page mid-stream cancels the request, or it keeps holding the node's invocation slot.
	useEffect(() => () => abortRef.current?.abort(), []);

	const run = async (): Promise<void> => {
		if (modelId === null || prompt.trim().length === 0) {
			return;
		}
		const request: DirectInferenceRequest = {
			modelId,
			prompt,
			systemPrompt: systemPrompt.trim().length > 0 ? systemPrompt : undefined,
			samplingOptions: toWireSamplingOptions(sampling),
		};
		const controller = new AbortController();
		abortRef.current = controller;
		setLastRequest(request);
		setRunning(true);
		let accumulated = emptyDirectInferenceResult;
		setResult(accumulated);
		try {
			await streamDirectInference(
				request,
				(event) => {
					accumulated = applyDirectInferenceEvent(accumulated, event);
					setResult(accumulated);
				},
				controller.signal,
			);
		} catch (error) {
			setResult({ ...accumulated, errorMessage: error instanceof Error ? error.message : String(error) });
		} finally {
			setRunning(false);
			abortRef.current = undefined;
		}
	};

	const errorText =
		result.errorCode === gpuWorkExclusiveCode
			? t(
					"pages.directInference.gpuWorkExclusive",
					"A training run, evaluation or export holds the GPU. Try again when it has finished.",
				)
			: result.errorMessage;

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
					<DirectInferenceSamplingFields value={sampling} onChange={setSampling} disabled={running} />
					<Group>
						<Button onClick={run} loading={running} disabled={modelId === null || prompt.trim().length === 0}>
							{t("pages.directInference.send", "Send")}
						</Button>
						{running && (
							<Button variant="default" onClick={() => abortRef.current?.abort()}>
								{t("pages.directInference.stop", "Stop")}
							</Button>
						)}
					</Group>
				</Stack>
			</Paper>
			{errorText && (
				<Alert color="red" title={t("pages.directInference.errorTitle", "The request failed")}>
					{errorText}
				</Alert>
			)}
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<Group justify="space-between">
						<Title order={4}>{t("pages.directInference.output", "Output")}</Title>
						<CopyButton value={JSON.stringify({ request: lastRequest, result }, null, 2)}>
							{({ copied, copy }) => (
								<Button size="xs" variant="default" onClick={copy} disabled={lastRequest === undefined}>
									{copied ? t("pages.directInference.copied", "Copied") : t("pages.directInference.copyJson", "Copy as JSON")}
								</Button>
							)}
						</CopyButton>
					</Group>
					{result.reasoning.length > 0 && (
						<Text size="sm" c="dimmed" style={{ whiteSpace: "pre-wrap" }} data-testid="direct-inference-reasoning">
							{result.reasoning}
						</Text>
					)}
					<Text style={{ whiteSpace: "pre-wrap" }} data-testid="direct-inference-output">
						{result.text}
					</Text>
					{result.finishReason === "length" && (
						<Alert color="yellow" data-testid="direct-inference-cut-off">
							{t(
								"pages.directInference.cutOff",
								"The answer was cut off, at the output limit or because the context is full. Raise Max output tokens for the first; shorten the prompt or load the model with a larger context for the second.",
							)}
						</Alert>
					)}
					<DirectInferenceMetrics result={result} />
				</Stack>
			</Paper>
		</PageShell>
	);
}
