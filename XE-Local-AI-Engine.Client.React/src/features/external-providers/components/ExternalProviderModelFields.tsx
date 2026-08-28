import { ActionIcon, Button, Checkbox, Flex, Group, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import type { Dispatch } from "react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import {
	type ExternalProviderFormAction,
	nextExternalRowId,
} from "@/features/external-providers/models/ExternalProviderFormState";
import {
	type ExternalProviderFormErrors,
	type ExternalProviderFormValues,
	externalReasoningEfforts,
} from "@/features/external-providers/models/ExternalProviderModel";

interface ExternalProviderModelFieldsProps {
	readonly values: ExternalProviderFormValues;
	readonly errors: ExternalProviderFormErrors;
	readonly modelRowIds: readonly string[];
	readonly dispatch: Dispatch<ExternalProviderFormAction>;
}

export function ExternalProviderModelFields({ values, errors, modelRowIds, dispatch }: ExternalProviderModelFieldsProps) {
	const { t } = useTranslation();

	const effortData = useMemo(
		() => [
			{ value: "", label: t("pages.externalProviders.models.effortUnset", "Server default") },
			...externalReasoningEfforts.map((effort) => ({
				value: effort,
				label: t(`pages.externalProviders.models.effortOptions.${effort}`, effort),
			})),
		],
		[t],
	);

	return (
		<Stack gap={6}>
			<Text size="sm" fw={500}>
				{t("pages.externalProviders.models.title", "Models")}
			</Text>
			<Text size="xs" c="dimmed">
				{t("pages.externalProviders.models.description")}
			</Text>

			{values.models.map((model, index) => {
				const effortEnabled = model.supportsReasoning && model.supportsReasoningEffort;
				return (
					<Stack key={modelRowIds[index]} gap={6} data-testid={`external-provider-model-${index}`}>
						<Group align="flex-end" gap="xs" wrap="nowrap">
							<Flex
								direction={{ base: "column", sm: "row" }}
								gap="xs"
								align={{ base: "stretch", sm: "flex-end" }}
								style={{ flex: "1 1 auto", minWidth: 0 }}
							>
								<TextInput
									style={{ flex: "1 1 auto", minWidth: 0 }}
									aria-label={t("pages.externalProviders.models.wireIdLabel", "Backing model id")}
									label={index === 0 ? t("pages.externalProviders.models.wireIdLabel", "Backing model id") : undefined}
									placeholder={t("pages.externalProviders.models.wireIdPlaceholder", "unsloth/Qwen3-27B-GGUF")}
									value={model.wireId}
									onChange={(event) => {
										const value = event.currentTarget.value;
										dispatch({ type: "setModelField", index, field: "wireId", value });
									}}
									onBlur={() => dispatch({ type: "touchField", field: "models" })}
								/>
								<TextInput
									style={{ flex: "1 1 auto", minWidth: 0 }}
									aria-label={t("pages.externalProviders.models.displayNameLabel", "Display name (optional)")}
									label={
										index === 0 ? t("pages.externalProviders.models.displayNameLabel", "Display name (optional)") : undefined
									}
									placeholder={t("pages.externalProviders.models.displayNamePlaceholder", "Qwen3 27B")}
									value={model.displayName}
									onChange={(event) => {
										const value = event.currentTarget.value;
										dispatch({ type: "setModelField", index, field: "displayName", value });
									}}
								/>
								<TextInput
									style={{ flex: "0 1 10rem", minWidth: 0 }}
									inputMode="numeric"
									aria-label={t("pages.externalProviders.models.contextLengthLabel", "Context length")}
									label={index === 0 ? t("pages.externalProviders.models.contextLengthLabel", "Context length") : undefined}
									placeholder={t("pages.externalProviders.models.contextLengthPlaceholder", "32768")}
									value={model.contextLength}
									onChange={(event) => {
										const value = event.currentTarget.value;
										dispatch({ type: "setModelField", index, field: "contextLength", value });
									}}
									onBlur={() => dispatch({ type: "touchField", field: "models" })}
								/>
							</Flex>
							<ActionIcon
								variant="subtle"
								color="red"
								size="lg"
								data-testid={`external-provider-remove-model-${index}`}
								aria-label={t("pages.externalProviders.models.removeModel", "Remove model")}
								onClick={() => dispatch({ type: "removeModel", index, replacementRowId: nextExternalRowId("external-model") })}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Group>

						<Group gap="md" wrap="wrap">
							<Checkbox
								size="xs"
								data-testid={`external-provider-model-tools-${index}`}
								label={t("pages.externalProviders.models.supportsTools", "Tools")}
								checked={model.supportsTools}
								onChange={() => dispatch({ type: "toggleModelFlag", index, flag: "supportsTools" })}
							/>
							<Checkbox
								size="xs"
								data-testid={`external-provider-model-vision-${index}`}
								label={t("pages.externalProviders.models.supportsVision", "Vision")}
								checked={model.supportsVision}
								onChange={() => dispatch({ type: "toggleModelFlag", index, flag: "supportsVision" })}
							/>
							<Checkbox
								size="xs"
								data-testid={`external-provider-model-reasoning-${index}`}
								label={t("pages.externalProviders.models.supportsReasoning", "Reasoning")}
								checked={model.supportsReasoning}
								onChange={() => dispatch({ type: "toggleModelFlag", index, flag: "supportsReasoning" })}
							/>
							<Checkbox
								size="xs"
								disabled={!model.supportsReasoning}
								data-testid={`external-provider-model-effort-${index}`}
								label={t("pages.externalProviders.models.supportsReasoningEffort", "Reasoning effort")}
								checked={model.supportsReasoningEffort}
								onChange={() => dispatch({ type: "toggleModelFlag", index, flag: "supportsReasoningEffort" })}
							/>
							<Select
								size="xs"
								style={{ width: "12rem" }}
								disabled={!effortEnabled}
								data-testid={`external-provider-model-default-effort-${index}`}
								aria-label={t("pages.externalProviders.models.defaultEffortLabel", "Default reasoning effort")}
								data={effortData}
								value={model.defaultReasoningEffort}
								onChange={(value) => dispatch({ type: "setModelEffort", index, value: (value ?? "") as ReasoningEffort | "" })}
							/>
						</Group>

						{/* D6: the tool declaration is not scoped to chat. A model declared tool-capable joins the node's
						    tool-capable allow-list, which the Agents page reads too. */}
						<Text size="xs" c="dimmed">
							{t("pages.externalProviders.models.toolsHelp")}
						</Text>
					</Stack>
				);
			})}

			{errors.models ? (
				<Text size="xs" c="red" data-testid="external-provider-models-error">
					{errors.models}
				</Text>
			) : null}

			<Group>
				<Button
					variant="light"
					size="xs"
					leftSection={<IconPlus size={14} />}
					data-testid="external-provider-add-model"
					onClick={() => dispatch({ type: "addModel", rowId: nextExternalRowId("external-model") })}
				>
					{t("pages.externalProviders.models.addModel", "Add model")}
				</Button>
			</Group>
		</Stack>
	);
}
