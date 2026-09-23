import { Accordion, NumberInput, Select, Stack, Textarea, TextInput } from "@mantine/core";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

import { samplingFieldGroups } from "@/core/runtime/ChatSamplingOptions";
import { GraphWorkflowInputBindingsField } from "@/features/graphWorkflows/components/config/GraphWorkflowInputBindingsField";
import { GraphWorkflowJsonField } from "@/features/graphWorkflows/components/config/GraphWorkflowJsonField";
import { withCurrentValue } from "@/features/graphWorkflows/components/config/GraphWorkflowSelectOptions";
import type { GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";

type LlmCallNodeData = Extract<GraphWorkflowCanvasNodeData, { kind: "LlmCall" }>;
const reasoningEfforts = ["none", "low", "medium", "high"] as const;

export function GraphWorkflowLlmCallConfigForm({
	node,
	onChange,
	errorFor,
	onTouch,
	modelOptions,
	readOnly = false,
}: {
	readonly node: LlmCallNodeData;
	readonly onChange: (patch: Partial<LlmCallNodeData>) => void;
	readonly errorFor: (field: string) => string | undefined;
	readonly onTouch: (field: string) => void;
	readonly modelOptions: readonly { readonly value: string; readonly label: string }[];
	readonly readOnly?: boolean;
}) {
	const { t } = useTranslation();
	const ready = useRef(false);
	useEffect(() => {
		ready.current = true;
	}, []);
	const samplingFields = samplingFieldGroups.flatMap((group) => group.fields).filter((meta) => meta.key !== "seed");

	return (
		<>
			<Select
				label={t("pages.graphWorkflows.config.model", "Model")}
				placeholder={t("pages.graphWorkflows.config.llmModelPlaceholder", "Node default local model")}
				data={withCurrentValue(modelOptions, node.model)}
				value={node.model}
				clearable={true}
				searchable={true}
				disabled={readOnly}
				onChange={(model) => onChange({ model })}
				data-testid="gw-node-config-llm-model"
			/>
			<Textarea
				label={t("pages.graphWorkflows.config.systemPrompt", "System prompt")}
				description={t("pages.graphWorkflows.config.systemPromptHelp", "Optional. A blank prompt is kept blank.")}
				value={node.systemPrompt ?? ""}
				disabled={readOnly}
				autosize={true}
				minRows={2}
				onChange={(event) => onChange({ systemPrompt: event.currentTarget.value })}
				data-testid="gw-node-config-system-prompt"
			/>
			<Textarea
				label={t("pages.graphWorkflows.config.llmPrompt", "Prompt")}
				value={node.prompt}
				disabled={readOnly}
				autosize={true}
				minRows={4}
				error={errorFor("prompt")}
				onBlur={() => onTouch("prompt")}
				onChange={(event) => onChange({ prompt: event.currentTarget.value })}
				data-testid="gw-node-config-llm-prompt"
			/>
			<Select
				label={t("pages.graphWorkflows.config.reasoningEffort", "Reasoning effort")}
				placeholder={t("pages.graphWorkflows.config.reasoningEffortPlaceholder", "Provider default")}
				data={reasoningEfforts.map((effort) => ({
					value: effort,
					label: t(`pages.graphWorkflows.config.effort.${effort}`, effort),
				}))}
				value={node.reasoningEffort}
				clearable={true}
				disabled={readOnly}
				onChange={(reasoningEffort) => onChange({ reasoningEffort })}
				data-testid="gw-node-config-llm-effort"
			/>
			<GraphWorkflowJsonField
				label={t("pages.graphWorkflows.config.responseJsonSchema", "Response JSON schema")}
				value={node.responseJsonSchema}
				error={errorFor("responseJsonSchema")}
				readOnly={readOnly}
				onChange={(responseJsonSchema) => {
					onTouch("responseJsonSchema");
					onChange({ responseJsonSchema });
				}}
				data-testid="gw-node-config-llm-response-schema"
			/>
			<GraphWorkflowInputBindingsField
				bindings={node.inputBindings}
				onChange={(inputBindings) => onChange({ inputBindings })}
				error={errorFor("inputBindings")}
				onTouch={() => onTouch("inputBindings")}
				readOnly={readOnly}
			/>
			<Accordion variant="contained">
				<Accordion.Item value="advanced">
					<Accordion.Control>{t("pages.graphWorkflows.config.advanced", "Advanced")}</Accordion.Control>
					<Accordion.Panel>
						<Stack gap="sm">
							{samplingFields.map((meta) => (
								<NumberInput
									key={meta.key}
									label={
										meta.key === "numCtx" ? t("pages.graphWorkflows.config.numCtx", "Prompt budget (tokens)") : t(meta.labelKey)
									}
									description={
										meta.key === "numCtx"
											? t(
													"pages.graphWorkflows.config.numCtxHelp",
													"Limits this request's prompt budget; it does not resize the server context window.",
												)
											: t(meta.descriptionKey)
									}
									value={(node.samplingOptions[meta.key] as number | undefined) ?? ""}
									min={meta.min}
									max={meta.max}
									step={meta.step}
									decimalScale={meta.decimalScale}
									allowDecimal={meta.allowDecimal}
									clampBehavior="none"
									disabled={readOnly}
									onChange={(value) => {
										if (ready.current) {
											onChange({
												samplingOptions: { ...node.samplingOptions, [meta.key]: typeof value === "number" ? value : undefined },
											});
										}
									}}
									data-testid={`gw-node-config-sampling-${meta.key}`}
								/>
							))}
							<TextInput
								label={t("pages.chat.samplingOptions.seed", "Seed")}
								description={t(
									"pages.chat.samplingOptions.seedDescription",
									"Random seed for reproducibility. -1 uses a random seed.",
								)}
								value={node.samplingOptions.seed ?? ""}
								disabled={readOnly}
								onChange={(event) => {
									if (ready.current) {
										onChange({ samplingOptions: { ...node.samplingOptions, seed: event.currentTarget.value || undefined } });
									}
								}}
								data-testid="gw-node-config-sampling-seed"
							/>
							<Textarea
								label={t("pages.chat.samplingOptions.stop", "Stop sequences")}
								value={(node.samplingOptions.stop ?? []).join("\n")}
								disabled={readOnly}
								onChange={(event) => {
									const next = event.currentTarget.value;
									onChange({
										samplingOptions: {
											...node.samplingOptions,
											stop: next.split("\n"),
										},
									});
								}}
								data-testid="gw-node-config-sampling-stop"
							/>
						</Stack>
					</Accordion.Panel>
				</Accordion.Item>
			</Accordion>
		</>
	);
}
