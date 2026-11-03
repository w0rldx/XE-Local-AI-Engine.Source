import { Alert, Button, Group, Paper, Select, SimpleGrid, Stack, Text, Textarea, Title } from "@mantine/core";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { DirectInferenceResult } from "@/core/directInference/DirectInferenceModels";
import { DirectInferenceErrorAlert, DirectInferenceOutput } from "@/core/directInference/DirectInferenceOutput";
import { DirectInferenceSamplingFields } from "@/core/directInference/DirectInferenceSamplingFields";
import { useDirectInferenceModels } from "@/core/directInference/useDirectInferenceModels";
import { type ChatSamplingOptions, toWireSamplingOptions } from "@/core/runtime/ChatSamplingOptions";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";
import { comparisonSeed, runInOrder } from "@/features/modelLab/models/SamePromptRun";

interface Comparison {
	models: [string, string];
	seed: number;
	// Null until that model's run starts.
	results: [DirectInferenceResult | null, DirectInferenceResult | null];
}

// One prompt, two installed llama.cpp models, one after the other over the chat hub. Held in this component only: nothing is saved.
export function SamePromptComparison() {
	const { t } = useTranslation();
	const { modelGroups, llamaCppModels, status } = useDirectInferenceModels();
	const modelItems = modelGroups.flatMap((group) => group.items).filter((item) => llamaCppModels.has(item.value));
	const [modelA, setModelA] = useState<string | null>(null);
	const [modelB, setModelB] = useState<string | null>(null);
	const [systemPrompt, setSystemPrompt] = useState("");
	const [prompt, setPrompt] = useState("");
	const [sampling, setSampling] = useState<ChatSamplingOptions>({});
	const [comparison, setComparison] = useState<Comparison | undefined>();
	const [running, setRunning] = useState(false);
	const abortRef = useRef<AbortController | undefined>(undefined);
	// Leaving mid-run cancels it, or it keeps holding the node's invocation slot.
	useEffect(() => () => abortRef.current?.abort(), []);

	const run = async (): Promise<void> => {
		if (modelA === null || modelB === null || prompt.trim().length === 0) {
			return;
		}
		const seed = comparisonSeed(sampling.seed);
		const shared = {
			prompt,
			systemPrompt: systemPrompt.trim().length > 0 ? systemPrompt : undefined,
			samplingOptions: toWireSamplingOptions({ ...sampling, seed }),
		};
		const controller = new AbortController();
		abortRef.current = controller;
		setComparison({ models: [modelA, modelB], seed, results: [null, null] });
		setRunning(true);
		await runInOrder(
			[
				{ ...shared, modelId: modelA },
				{ ...shared, modelId: modelB },
			],
			(index, result) =>
				setComparison(
					(current) =>
						current && {
							...current,
							results: index === 0 ? [result, current.results[1]] : [current.results[0], result],
						},
				),
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

	return (
		<Stack gap="md">
			<Alert color="blue" data-testid="model-lab-compare-honesty">
				<Text size="sm">
					{t(
						"pages.modelLab.compare.notIdentical",
						"Identical settings do not guarantee identical results: a model file, runtime variant or background load can still change the output.",
					)}
				</Text>
				<Text size="sm">
					{t(
						"pages.modelLab.compare.notEvidence",
						"A difference alone is not evidence of a meaningful effect. Output also changes for reasons unrelated to what you are comparing.",
					)}
				</Text>
				<Text size="sm">
					{t(
						"pages.modelLab.compare.scope",
						"Both runs use this node and its current llama.cpp build, one after the other. Timings of a run can include loading its model.",
					)}
				</Text>
			</Alert>
			<ModelPickerNotice status={status} modelCount={modelItems.length} />
			<Paper withBorder={true} p="md">
				<Stack gap="sm">
					<SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
						<Select
							label={t("pages.modelLab.compare.modelA", "Model A")}
							placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
							data={modelItems}
							value={modelA}
							onChange={setModelA}
							searchable={true}
							disabled={running}
						/>
						<Select
							label={t("pages.modelLab.compare.modelB", "Model B")}
							placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
							data={modelItems}
							value={modelB}
							onChange={setModelB}
							searchable={true}
							disabled={running}
						/>
					</SimpleGrid>
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
						<Button onClick={run} loading={running} disabled={modelA === null || modelB === null || prompt.trim().length === 0}>
							{t("pages.modelLab.compare.run", "Run both")}
						</Button>
						{running && (
							<Button variant="default" onClick={stop}>
								{t("pages.directInference.stop", "Stop")}
							</Button>
						)}
					</Group>
				</Stack>
			</Paper>
			{comparison && (
				<>
					<Text size="sm" c="dimmed" data-testid="model-lab-compare-seed">
						{t("pages.modelLab.compare.seed", "Seed for both runs: {{seed}}", { seed: comparison.seed })}
					</Text>
					<SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
						{comparison.results.map((result, index) => (
							<Paper key={index === 0 ? "a" : "b"} withBorder={true} p="md" data-testid={`model-lab-compare-${index}`}>
								<Stack gap="sm">
									<Title order={5} style={{ overflowWrap: "anywhere" }}>
										{comparison.models[index]}
									</Title>
									{result === null ? (
										<Text size="sm" c="dimmed">
											{running
												? t("pages.modelLab.compare.waiting", "Waiting for the first run to finish.")
												: t("pages.modelLab.compare.notRun", "Not run: stopped before this model started.")}
										</Text>
									) : (
										<>
											<DirectInferenceErrorAlert result={result} />
											<DirectInferenceOutput result={result} />
										</>
									)}
								</Stack>
							</Paper>
						))}
					</SimpleGrid>
				</>
			)}
		</Stack>
	);
}
