import { Alert, Badge, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { DevWorkflowGraphView } from "@/features/devWorkflows/components/DevWorkflowGraphView";
import { toDevWorkflowDefinitionCanvasGraph } from "@/features/devWorkflows/models/DevWorkflowGraphModels";
import { useDevWorkflowDefinition } from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowDefinitionPanelProps {
	readonly definitionId: string | undefined;
	/** The picker's own label, so the header names the template before its graph has loaded. */
	readonly definitionName?: string;
}

/**
 * A template's shape, before anything has run it. Same canvas as the run view (P4 §4: one component, two data
 * sources) — the cards carry no status, because a definition has none and a `Pending` badge on every node would
 * claim the template is a run waiting on its dependencies.
 *
 * Read-only in slices A–C by ruling (N1): the definition EDITOR is a form in slice D, not a canvas, so nothing here
 * offers a control that would imply otherwise.
 */
export function DevWorkflowDefinitionPanel({ definitionId, definitionName }: DevWorkflowDefinitionPanelProps) {
	const { t } = useTranslation();
	const definitionQuery = useDevWorkflowDefinition(definitionId);
	const definition = definitionQuery.data;
	const graph = useMemo(() => toDevWorkflowDefinitionCanvasGraph(definition?.graph), [definition?.graph]);

	if (!definitionId) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.definition.pick", "Pick a template to see the workflow it will run.")}
				data-testid="dev-workflow-definition-empty"
			/>
		);
	}

	if (definitionQuery.isPending) {
		return <Loader size="sm" data-testid="dev-workflow-definition-loading" />;
	}

	if (definitionQuery.isError) {
		return (
			<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-definition-error">
				{apiErrorMessage(definitionQuery.error, t("pages.devWorkflows.definition.loadFailed", "Could not load this template."))}
			</Alert>
		);
	}

	return (
		<Stack gap="xs" h="100%" style={{ minHeight: 0 }} data-testid="dev-workflow-definition-panel">
			<Group gap="xs" wrap="wrap">
				<Text fw={600} size="sm" data-testid="dev-workflow-definition-name">
					{definition?.name ?? definitionName ?? ""}
				</Text>
				<Badge size="xs" variant="light" color="gray">
					{t("pages.devWorkflows.definition.version", "v{{version}}", { version: definition?.version ?? 1 })}
				</Badge>
				<Text size="xs" c="dimmed" data-testid="dev-workflow-definition-preview-note">
					{t("pages.devWorkflows.definition.preview", "Nothing has run yet — this is the template, not a run.")}
				</Text>
			</Group>
			<div style={{ flex: 1, minHeight: 240 }}>
				{/* No selection: a definition node has no node-run to drill into, so a click would resolve nothing. */}
				<DevWorkflowGraphView graph={graph} onSelect={() => undefined} />
			</div>
		</Stack>
	);
}
