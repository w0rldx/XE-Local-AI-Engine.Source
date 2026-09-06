// Name and description for a definition — the two fields that are NOT part of the graph document. Used both to create
// a workflow and to rename one ("Save as" reuses it with a different title and submit label), so it owns no mutation
// and no query: the page decides which of the two a submit means.

import { Button, Group, Stack, Textarea, TextInput } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { z } from "zod";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

/** `GraphWorkflowDefinition.Name` is bounded server-side; the message is a full i18n key, like the graph schemas'. */
const metaSchema = z.object({
	name: z
		.string()
		.trim()
		.min(1, { message: "pages.graphWorkflows.form.name.required" })
		.max(120, { message: "pages.graphWorkflows.form.name.tooLong" }),
	description: z.string(),
});

export interface GraphWorkflowDefinitionMetaDialogProps {
	readonly opened: boolean;
	readonly initial?: { readonly name: string; readonly description?: string | null };
	readonly title: string;
	readonly submitLabel: string;
	readonly isSubmitting?: boolean;
	readonly onSubmit: (values: { name: string; description: string | null }) => void;
	readonly onClose: () => void;
}

export function GraphWorkflowDefinitionMetaDialog({
	opened,
	initial,
	title,
	submitLabel,
	isSubmitting = false,
	onSubmit,
	onClose,
}: GraphWorkflowDefinitionMetaDialogProps) {
	const { t } = useTranslation();
	const [name, setName] = useState(initial?.name ?? "");
	const [description, setDescription] = useState(initial?.description ?? "");
	const [error, setError] = useState<string | undefined>(undefined);

	// Reseeded on every OPEN, so a cancelled edit does not survive into the next one and a "Save as" opened over a
	// renamed definition starts from the name it has now.
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on the open transition, not on `initial`.
	useEffect(() => {
		if (opened) {
			setName(initial?.name ?? "");
			setDescription(initial?.description ?? "");
			setError(undefined);
		}
	}, [opened]);

	const handleSubmit = (): void => {
		const result = metaSchema.safeParse({ name, description });
		if (!result.success) {
			setError(t(result.error.issues[0]?.message ?? "pages.graphWorkflows.form.name.required", "Enter a name."));
			return;
		}
		setError(undefined);
		const trimmed = result.data.description.trim();
		onSubmit({ name: result.data.name, description: trimmed.length > 0 ? trimmed : null });
	};

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={title}
			data-testid="gw-definition-meta-dialog"
			footer={
				<Group gap="sm">
					<Button variant="default" onClick={onClose} data-testid="gw-definition-meta-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button loading={isSubmitting} onClick={handleSubmit} data-testid="gw-definition-meta-submit">
						{submitLabel}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				<TextInput
					label={t("pages.graphWorkflows.definitions.nameLabel", "Name")}
					placeholder={t("pages.graphWorkflows.definitions.namePlaceholder", "Nightly triage")}
					value={name}
					required={true}
					maxLength={120}
					error={error}
					onChange={(event) => setName(event.currentTarget.value)}
					data-testid="gw-definition-meta-name"
				/>
				<Textarea
					label={t("pages.graphWorkflows.definitions.descriptionLabel", "Description")}
					placeholder={t("pages.graphWorkflows.definitions.descriptionPlaceholder", "What this workflow is for.")}
					value={description}
					autosize={true}
					minRows={2}
					maxRows={6}
					onChange={(event) => setDescription(event.currentTarget.value)}
					data-testid="gw-definition-meta-description"
				/>
			</Stack>
		</DialogShell>
	);
}
