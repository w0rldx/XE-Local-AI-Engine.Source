import { Stack, Textarea, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";
import type { AgentDefinitionFormValues } from "@/features/agents/models/AgentDefinitionModels";

interface AgentDefinitionBasicFieldsProps {
	values: AgentDefinitionFormValues;
	nameError: string | undefined;
	instructionsError: string | undefined;
	onFieldChange: (updater: (current: AgentDefinitionFormValues) => AgentDefinitionFormValues) => void;
}

// Name, description, and instructions inputs — the identity/persona section of the agent definition form.
export function AgentDefinitionBasicFields({ values, nameError, instructionsError, onFieldChange }: AgentDefinitionBasicFieldsProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="md">
			<TextInput
				label={t("pages.agents.form.name.label", "Name")}
				placeholder={t("pages.agents.form.name.placeholder", "Research assistant")}
				value={values.name}
				required={true}
				error={nameError ? t("pages.agents.form.name.required", "Name is required") : undefined}
				onChange={(event) => {
					const value = event.currentTarget.value;
					onFieldChange((current) => ({ ...current, name: value }));
				}}
				data-testid="agent-form-name"
			/>
			<Textarea
				label={t("pages.agents.form.description.label", "Description")}
				placeholder={t("pages.agents.form.description.placeholder", "Optional short summary")}
				value={values.description}
				autosize={true}
				minRows={2}
				onChange={(event) => {
					const value = event.currentTarget.value;
					onFieldChange((current) => ({ ...current, description: value }));
				}}
				data-testid="agent-form-description"
			/>
			<MarkdownEditorField
				label={t("pages.agents.form.instructions.label", "Instructions")}
				description={t("pages.agents.form.instructions.description", "System prompt that defines how this agent behaves.")}
				placeholder={t("pages.agents.form.instructions.placeholder", "You are a helpful assistant that…")}
				value={values.instructions}
				required={true}
				minRows={4}
				error={instructionsError ? t("pages.agents.form.instructions.required", "Instructions are required") : undefined}
				onChange={(value) => {
					onFieldChange((current) => ({ ...current, instructions: value }));
				}}
				data-testid="agent-form-instructions"
			/>
		</Stack>
	);
}
