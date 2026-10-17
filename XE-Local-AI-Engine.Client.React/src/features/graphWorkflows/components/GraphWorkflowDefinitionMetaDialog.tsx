// Name and description for a definition — the two fields that are NOT part of the graph document. Used both to create
// a workflow and to rename one ("Save as" reuses it with a different title and submit label), so it owns no mutation
// and no query: the page decides which of the two a submit means.

import { Alert, Button, Checkbox, Group, Select, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { z } from "zod";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { graphWorkflowTriggerSources } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { GRAPH_WORKFLOW_SAMPLES } from "@/features/graphWorkflows/samples/GraphWorkflowSamples";

/** `GraphWorkflowRequestLimits.MaxNameLength`, verbatim; the message is a full i18n key, like the graph schemas'. */
const metaSchema = z.object({
	name: z
		.string()
		.trim()
		.min(1, { message: "pages.graphWorkflows.form.name.required" })
		.max(200, { message: "pages.graphWorkflows.form.name.tooLong" }),
	description: z.string(),
});

/** The Select's value for "no sample"; a Select option needs a non-empty value. */
const BLANK = "blank";

export interface GraphWorkflowDefinitionMetaDialogProps {
	readonly opened: boolean;
	readonly initial?: { readonly name: string; readonly description?: string | null };
	readonly title: string;
	readonly submitLabel: string;
	readonly isSubmitting?: boolean;
	/** Shows the "Start from" sample picker. Only a create has no graph of its own to start from. */
	readonly offerSamples?: boolean;
	/** An imported graph's web_fetch allowlist, named before create so the operator sees what it will fetch. */
	readonly fetchesUnder?: readonly string[];
	/** The stored `allowsActions` of the definition being edited; undefined hides the switch (create, save as and import never set it). */
	readonly allowsActions?: boolean;
	/** The stored `triggerSources` of the definition being edited; undefined hides the checkboxes, like the switch. */
	readonly triggerSources?: readonly string[];
	readonly onSubmit: (values: {
		name: string;
		description: string | null;
		sampleId: string | null;
		allowsActions: boolean | null;
		triggerSources: string[] | null;
	}) => void;
	readonly onClose: () => void;
}

export function GraphWorkflowDefinitionMetaDialog({
	opened,
	initial,
	title,
	submitLabel,
	isSubmitting = false,
	offerSamples = false,
	fetchesUnder = [],
	allowsActions,
	triggerSources,
	onSubmit,
	onClose,
}: GraphWorkflowDefinitionMetaDialogProps) {
	const { t } = useTranslation();
	const [name, setName] = useState(initial?.name ?? "");
	const [description, setDescription] = useState(initial?.description ?? "");
	const [error, setError] = useState<string | undefined>(undefined);
	const [sampleId, setSampleId] = useState<string | null>(null);
	const [actions, setActions] = useState(allowsActions ?? false);
	const [sources, setSources] = useState<string[]>([...(triggerSources ?? [])]);
	const [sourcesError, setSourcesError] = useState<string | undefined>(undefined);
	// What the last pick wrote into the two fields. A field still holding that value (or nothing) is the sample's to
	// replace; anything else is the operator's typing and is left alone.
	const [prefilled, setPrefilled] = useState({ name: "", description: "" });

	// Reseeded on every OPEN, so a cancelled edit does not survive into the next one and a "Save as" opened over a
	// renamed definition starts from the name it has now.
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on the open transition, not on `initial`.
	useEffect(() => {
		if (opened) {
			setName(initial?.name ?? "");
			setDescription(initial?.description ?? "");
			setError(undefined);
			setSampleId(null);
			setActions(allowsActions ?? false);
			setSources([...(triggerSources ?? [])]);
			setSourcesError(undefined);
			setPrefilled({ name: "", description: "" });
		}
	}, [opened]);

	const pickSample = (value: string | null): void => {
		const sample = GRAPH_WORKFLOW_SAMPLES.find((entry) => entry.id === value);
		const next = sample
			? { name: t(sample.nameKey, sample.defaultName), description: t(sample.descriptionKey, sample.defaultDescription) }
			: { name: "", description: "" };
		if (name === "" || name === prefilled.name) {
			setName(next.name);
		}
		if (description === "" || description === prefilled.description) {
			setDescription(next.description);
		}
		setPrefilled(next);
		setSampleId(sample?.id ?? null);
	};

	const selectedSample = GRAPH_WORKFLOW_SAMPLES.find((entry) => entry.id === sampleId);

	const handleSubmit = (): void => {
		const result = metaSchema.safeParse({ name, description });
		if (!result.success) {
			setError(t(result.error.issues[0]?.message ?? "pages.graphWorkflows.form.name.required", "Enter a name."));
			return;
		}
		setError(undefined);
		if (triggerSources !== undefined && sources.length === 0) {
			setSourcesError(t("pages.graphWorkflows.definitions.triggerSourcesRequired", "Choose at least one source."));
			return;
		}
		setSourcesError(undefined);
		// Declaration order, so "unchanged" is a plain comparison and the request reads like the response.
		const ordered = graphWorkflowTriggerSources.filter((source) => sources.includes(source));
		const sourcesChanged =
			triggerSources !== undefined &&
			(ordered.length !== triggerSources.length || ordered.some((source) => !triggerSources.includes(source)));
		const trimmed = result.data.description.trim();
		onSubmit({
			name: result.data.name,
			description: trimmed.length > 0 ? trimmed : null,
			sampleId,
			// Null unless the switch is shown and moved, so a plain rename never writes the flag.
			allowsActions: allowsActions === undefined || actions === allowsActions ? null : actions,
			// Null unless the checkboxes are shown and changed, for the same reason.
			triggerSources: sourcesChanged ? ordered : null,
		});
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
				{offerSamples ? (
					<Select
						label={t("pages.graphWorkflows.samples.startFromLabel", "Start from")}
						description={
							selectedSample
								? t(selectedSample.descriptionKey, selectedSample.defaultDescription)
								: t(
										"pages.graphWorkflows.samples.startFromDescription",
										"A sample is a complete workflow you can run right away with any installed chat model.",
									)
						}
						data={[
							{ value: BLANK, label: t("pages.graphWorkflows.samples.blank", "Blank workflow") },
							...GRAPH_WORKFLOW_SAMPLES.map((sample) => ({ value: sample.id, label: t(sample.nameKey, sample.defaultName) })),
						]}
						value={sampleId ?? BLANK}
						allowDeselect={false}
						onChange={pickSample}
						data-testid="gw-definition-meta-sample"
					/>
				) : null}
				<TextInput
					label={t("pages.graphWorkflows.definitions.nameLabel", "Name")}
					placeholder={t("pages.graphWorkflows.definitions.namePlaceholder", "Nightly triage")}
					value={name}
					required={true}
					maxLength={200}
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
				{allowsActions === undefined ? null : (
					<Stack gap="xs">
						<Switch
							label={t("pages.graphWorkflows.definitions.allowActionsLabel", "Allow actions")}
							description={t(
								"pages.graphWorkflows.definitions.allowActionsDescription",
								"Tool nodes may run custom tools flagged for unattended use.",
							)}
							checked={actions}
							onChange={(event) => setActions(event.currentTarget.checked)}
							data-testid="gw-definition-meta-allow-actions"
						/>
						{actions ? (
							<Alert color="orange" variant="light" data-testid="gw-definition-meta-allow-actions-warning">
								{t(
									"pages.graphWorkflows.definitions.allowActionsWarning",
									"An action changes something outside this app, such as switching a light or starting a program, with nobody asked first. Each one runs once per run and is audited before it runs. Only allow this for workflows you trust.",
								)}
							</Alert>
						) : null}
					</Stack>
				)}
				{triggerSources === undefined ? null : (
					<Checkbox.Group
						label={t("pages.graphWorkflows.definitions.triggerSourcesLabel", "Who may start a run")}
						description={t(
							"pages.graphWorkflows.definitions.triggerSourcesDescription",
							"Each start names its source; a source left out here is refused.",
						)}
						value={sources}
						onChange={setSources}
						error={sourcesError}
						data-testid="gw-definition-meta-trigger-sources"
					>
						<Group gap="md" mt="xs">
							{graphWorkflowTriggerSources.map((source) => (
								<Checkbox
									key={source}
									value={source}
									label={t(`pages.graphWorkflows.triggerSource.${source}`, source)}
									data-testid={`gw-definition-meta-trigger-source-${source}`}
								/>
							))}
						</Group>
					</Checkbox.Group>
				)}
				{fetchesUnder.length > 0 ? (
					<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }} data-testid="gw-definition-meta-fetches-under">
						{t("pages.graphWorkflows.importFile.fetchesUnder", "This workflow fetches pages under: {{urls}}", {
							urls: fetchesUnder.join(", "),
						})}
					</Text>
				) : null}
			</Stack>
		</DialogShell>
	);
}
