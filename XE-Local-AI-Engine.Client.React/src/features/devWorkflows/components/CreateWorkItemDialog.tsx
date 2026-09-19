import { Button, Group, Select, Stack } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { WorkItemFields } from "@/features/devWorkflows/components/WorkItemFields";
import type {
	CreateWorkItemValues,
	DevelopmentProjectOption,
	DevWorkflowDefinitionSummaryResponse,
} from "@/features/devWorkflows/models/DevWorkflowModels";

export interface CreateWorkItemDialogProps {
	readonly opened: boolean;
	readonly definitions: readonly DevWorkflowDefinitionSummaryResponse[];
	readonly projects: readonly DevelopmentProjectOption[];
	readonly isSubmitting: boolean;
	readonly errorMessage?: string;
	readonly onClose: () => void;
	readonly onSubmit: (values: CreateWorkItemValues) => void;
}

/**
 * Creating a work item and starting its run are TWO calls: a work item is definition-agnostic at creation.
 * This dialog collects both halves and the page makes the calls in order — a failure between them leaves a
 * definition-less work item, which is a legal state whose detail page offers "start a run".
 *
 * The template picker shows the seeded definitions by their own names ("Research → Plan → Approval" and
 * "Feature Development v1"); it does not invent labels for them.
 */
export function CreateWorkItemDialog({
	opened,
	definitions,
	projects,
	isSubmitting,
	errorMessage,
	onClose,
	onSubmit,
}: CreateWorkItemDialogProps) {
	const { t } = useTranslation();
	const [title, setTitle] = useState("");
	const [request, setRequest] = useState("");
	const [definitionId, setDefinitionId] = useState<string | null>(null);
	const [projectId, setProjectId] = useState<string | null>(null);

	const trimmedTitle = title.trim();
	const trimmedRequest = request.trim();
	const canSubmit = trimmedTitle.length > 0 && trimmedRequest.length > 0 && Boolean(definitionId) && !isSubmitting;

	const close = (): void => {
		setTitle("");
		setRequest("");
		setDefinitionId(null);
		setProjectId(null);
		onClose();
	};

	return (
		<DialogShell
			opened={opened}
			onClose={close}
			title={t("pages.devWorkflows.create.title", "New work item")}
			data-testid="create-dev-workflow-work-item-dialog"
			// Anything typed here is unsaved until the create succeeds, so a stray overlay click must not discard it.
			confirmCloseWhen={trimmedTitle.length > 0 || trimmedRequest.length > 0}
			footer={
				<Group justify="flex-end">
					<Button variant="subtle" onClick={close} data-testid="create-dev-workflow-work-item-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						onClick={() =>
							onSubmit({
								title: trimmedTitle,
								request: trimmedRequest,
								developmentProjectId: projectId ?? undefined,
								definitionId: definitionId ?? "",
							})
						}
						disabled={!canSubmit}
						loading={isSubmitting}
						data-testid="create-dev-workflow-work-item-submit"
					>
						{t("pages.devWorkflows.create.submit", "Create and start")}
					</Button>
				</Group>
			}
		>
			<Stack gap="md">
				{errorMessage ? (
					<InlineErrorAlert message={errorMessage} variant="light" data-testid="create-dev-workflow-work-item-error" />
				) : null}
				<WorkItemFields
					values={{ title, request }}
					onChange={(next) => {
						if (next.title !== undefined) {
							setTitle(next.title);
						}
						if (next.request !== undefined) {
							setRequest(next.request);
						}
					}}
					testIdPrefix="create-dev-workflow-work-item"
				/>
				<Select
					label={t("pages.devWorkflows.create.definitionLabel", "Workflow template")}
					description={t("pages.devWorkflows.create.definitionHint", "The run executes a pinned copy of this template.")}
					placeholder={t("pages.devWorkflows.create.definitionPlaceholder", "Pick a template")}
					data={definitions.map((definition) => ({ value: definition.id ?? "", label: definition.name ?? "" }))}
					value={definitionId}
					required={true}
					onChange={setDefinitionId}
					data-testid="create-dev-workflow-work-item-definition"
				/>
				{/* The project stays optional — a research/plan-only workflow needs no repository. A run whose graph
				    does contain repo-bound nodes is refused at start with a 400 that says so. */}
				<Select
					label={t("pages.devWorkflows.create.projectLabel", "Development project (optional)")}
					description={t(
						"pages.devWorkflows.create.projectHint",
						"Only needed for workflows that build or validate code. Research and planning workflows need none.",
					)}
					placeholder={t("pages.devWorkflows.create.projectPlaceholder", "No project")}
					data={projects.map((project) => ({ value: project.id, label: project.label }))}
					value={projectId}
					clearable={true}
					onChange={setProjectId}
					data-testid="create-dev-workflow-work-item-project"
				/>
			</Stack>
		</DialogShell>
	);
}
