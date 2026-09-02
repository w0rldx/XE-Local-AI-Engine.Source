import { Alert, Button, Group, MultiSelect, Stack, Switch, Text, TextInput, Textarea } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { type DevWorkflowRuleSetResponse, devWorkflowNodeTypes } from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * Server-side limits (`DevWorkflowRequestLimits`), mirrored so the operator is stopped by the input rather than by a
 * 400 after writing four thousand characters of policy. The body ceiling is the load-bearing one: it exists because a
 * body the objective then has to cut is policy the operator believed was in force and the agent never fully read.
 */
const NAME_MAX = 255;
const DESCRIPTION_MAX = 1024;
const BODY_MAX = 4096;

export interface DevWorkflowRuleSetValues {
	readonly name: string;
	readonly description: string;
	readonly body: string;
	readonly projectIds: readonly string[];
	readonly nodeTypes: readonly string[];
	readonly enabled: boolean;
}

export interface DevWorkflowProjectOption {
	readonly id: string;
	readonly label: string;
}

export interface DevWorkflowRuleSetDialogProps {
	readonly opened: boolean;
	/** The rule set being edited, or undefined for a new one. Absent while its body is still loading. */
	readonly ruleSet?: DevWorkflowRuleSetResponse;
	readonly isLoading?: boolean;
	readonly projects: readonly DevWorkflowProjectOption[];
	readonly isSubmitting: boolean;
	/** Rendered inline (this feature has no toast pattern). A 409 arrives here as "changed elsewhere — reload". */
	readonly errorMessage?: string;
	readonly onClose: () => void;
	readonly onSubmit: (values: DevWorkflowRuleSetValues) => void;
}

const emptyValues: DevWorkflowRuleSetValues = {
	name: "",
	description: "",
	body: "",
	projectIds: [],
	nodeTypes: [],
	enabled: true,
};

function toValues(ruleSet: DevWorkflowRuleSetResponse | undefined): DevWorkflowRuleSetValues {
	if (!ruleSet) {
		return emptyValues;
	}
	return {
		name: ruleSet.name ?? "",
		description: ruleSet.description ?? "",
		body: ruleSet.body ?? "",
		projectIds: ruleSet.scope?.projectIds ?? [],
		nodeTypes: ruleSet.scope?.nodeTypes ?? [],
		enabled: ruleSet.enabled ?? true,
	};
}

/**
 * Create / edit one rule set. The body is markdown injected verbatim into a matching node's objective (Y2), so it is
 * edited in the same `CodeEditor` the artifact viewer reads with, not in a textarea that would reflow it.
 *
 * Both scope axes are ANDed by the resolver and an EMPTY axis means "every value" — a rule set with no scope at all
 * applies everywhere, which is why the placeholders say so rather than reading as an unset field the operator forgot.
 */
export function DevWorkflowRuleSetDialog({
	opened,
	ruleSet,
	isLoading = false,
	projects,
	isSubmitting,
	errorMessage,
	onClose,
	onSubmit,
}: DevWorkflowRuleSetDialogProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<DevWorkflowRuleSetValues>(emptyValues);

	// The edited rule set's body arrives a request AFTER the dialog opens, so the form is seeded when it lands rather
	// than at mount. Keyed on the row's identity and version so a reopen — or a save that bumped the version — reseeds,
	// while typing into the open form does not.
	const seedKey = `${ruleSet?.id ?? ""}:${ruleSet?.version ?? 0}:${opened}`;
	// biome-ignore lint/correctness/useExhaustiveDependencies: seeding is keyed on identity, not on the row object.
	useEffect(() => {
		setValues(toValues(ruleSet));
	}, [seedKey]);

	const trimmedName = values.name.trim();
	const trimmedBody = values.body.trim();
	const isTooLong = values.body.length > BODY_MAX;
	const canSubmit = trimmedName.length > 0 && trimmedBody.length > 0 && !isTooLong && !isSubmitting && !isLoading;

	const patch = (next: Partial<DevWorkflowRuleSetValues>): void => setValues((current) => ({ ...current, ...next }));

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			title={
				ruleSet
					? t("pages.devWorkflows.ruleSets.editTitle", "Edit rule set")
					: t("pages.devWorkflows.ruleSets.createTitle", "New rule set")
			}
			data-testid="dev-workflow-rule-set-dialog"
			// Anything typed here is unsaved until the write succeeds, so a stray overlay click must not discard it.
			confirmCloseWhen={trimmedName.length > 0 || trimmedBody.length > 0}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={onClose} data-testid="dev-workflow-rule-set-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						onClick={() => onSubmit({ ...values, name: trimmedName, body: values.body })}
						disabled={!canSubmit}
						loading={isSubmitting}
						data-testid="dev-workflow-rule-set-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{errorMessage ? (
					<Alert color="red" variant="light" data-testid="dev-workflow-rule-set-error">
						{errorMessage}
					</Alert>
				) : null}
				<TextInput
					label={t("pages.devWorkflows.ruleSets.nameLabel", "Name")}
					value={values.name}
					maxLength={NAME_MAX}
					required={true}
					onChange={(event) => patch({ name: event.currentTarget.value })}
					data-testid="dev-workflow-rule-set-name"
				/>
				<Textarea
					label={t("pages.devWorkflows.ruleSets.descriptionLabel", "Description")}
					description={t("pages.devWorkflows.ruleSets.descriptionHint", "For the catalogue only. It is never sent to an agent.")}
					value={values.description}
					maxLength={DESCRIPTION_MAX}
					autosize={true}
					minRows={2}
					maxRows={4}
					onChange={(event) => patch({ description: event.currentTarget.value })}
					data-testid="dev-workflow-rule-set-description"
				/>
				<Switch
					label={t("pages.devWorkflows.ruleSets.enabledLabel", "Enabled")}
					description={t(
						"pages.devWorkflows.ruleSets.enabledHint",
						"A disabled rule set stays in the catalogue and is injected into nothing.",
					)}
					checked={values.enabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						patch({ enabled: checked });
					}}
					data-testid="dev-workflow-rule-set-enabled"
				/>
				<MultiSelect
					label={t("pages.devWorkflows.ruleSets.projectsLabel", "Development projects")}
					description={t("pages.devWorkflows.ruleSets.projectsHint", "Leave empty to apply to every project.")}
					placeholder={t("pages.devWorkflows.ruleSets.projectsPlaceholder", "Every project")}
					data={projects.map((project) => ({ value: project.id, label: project.label }))}
					value={[...values.projectIds]}
					onChange={(next) => patch({ projectIds: next })}
					searchable={true}
					clearable={true}
					data-testid="dev-workflow-rule-set-projects"
				/>
				{/* A CLOSED token set, refused at the door by the server: a token nothing parses would match nothing,
				    silently, for the whole life of the rule set. So this is a picker, never free text. */}
				<MultiSelect
					label={t("pages.devWorkflows.ruleSets.nodeTypesLabel", "Node types")}
					description={t("pages.devWorkflows.ruleSets.nodeTypesHint", "Leave empty to apply to every node type.")}
					placeholder={t("pages.devWorkflows.ruleSets.nodeTypesPlaceholder", "Every node type")}
					data={devWorkflowNodeTypes.map((nodeType) => ({
						value: nodeType,
						label: t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType),
					}))}
					value={[...values.nodeTypes]}
					onChange={(next) => patch({ nodeTypes: next })}
					clearable={true}
					data-testid="dev-workflow-rule-set-node-types"
				/>
				<Stack gap={4}>
					<Text size="sm" fw={500}>
						{t("pages.devWorkflows.ruleSets.bodyLabel", "Body")}
					</Text>
					<Text size="xs" c="dimmed">
						{t(
							"pages.devWorkflows.ruleSets.bodyHint",
							"Markdown, injected verbatim into a matching node's objective. {{used}} of {{max}} characters.",
							{ used: values.body.length, max: BODY_MAX },
						)}
					</Text>
					<CodeEditor
						value={values.body}
						language="markdown"
						readOnly={false}
						height={280}
						wordWrap={true}
						aria-label={t("pages.devWorkflows.ruleSets.bodyLabel", "Body")}
						onChange={(next) => patch({ body: next })}
						data-testid="dev-workflow-rule-set-body"
					/>
					{isTooLong ? (
						<Alert color="red" variant="light" data-testid="dev-workflow-rule-set-body-too-long">
							{t("pages.devWorkflows.ruleSets.bodyTooLong", "The body is over the {{max}}-character limit and cannot be saved.", {
								max: BODY_MAX,
							})}
						</Alert>
					) : null}
				</Stack>
			</Stack>
		</DialogShell>
	);
}
