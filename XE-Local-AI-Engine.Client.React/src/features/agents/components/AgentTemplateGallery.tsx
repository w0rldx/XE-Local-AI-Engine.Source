import { Alert, Badge, Button, Checkbox, Group, Loader, Stack, Text, Title, Tooltip } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import { type AgentTemplateSummary, isOverTokenBudget } from "@/features/agents/models/AgentTemplateModels";
import { useAgentTemplates, useImportAgentTemplates } from "@/features/agents/queries/useAgentTemplates";

interface AgentTemplateGalleryProps {
	opened: boolean;
	onClose: () => void;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// Groups the flat summary list by division, preserving first-seen division order so the gallery sections are stable.
function groupByDivision(templates: readonly AgentTemplateSummary[]): [string, AgentTemplateSummary[]][] {
	const groups = new Map<string, AgentTemplateSummary[]>();
	for (const template of templates) {
		const division = template.division ?? "";
		const bucket = groups.get(division);
		if (bucket) {
			bucket.push(template);
		} else {
			groups.set(division, [template]);
		}
	}
	return [...groups.entries()];
}

// Operator-triggered starter-agent gallery. Lists the curated, vendored personas grouped by division; the operator
// selects any subset and imports them into editable AgentDefinition rows in one click. Already-imported slugs are
// disabled. Each row shows an estimated-token badge that warns (with a tooltip) when the persona exceeds the soft
// budget — cloud-tuned prompts can be heavy for local small models. Import success clears the selection and toasts a
// summary; the hook invalidates both the definitions and templates lists so the new agents appear and the disabled
// state refreshes.
//
// Layout: DialogShell owns the scroll region and sticky footer slot — Cancel and Import buttons live in the footer
// prop so they stay visible regardless of list length. The token Badge is flex-shrink:0 and the Checkbox column
// takes the slack (flex:1 minWidth:0) so long descriptions wrap instead of squeezing the badge into an ellipsis.
export function AgentTemplateGallery({ opened, onClose }: AgentTemplateGalleryProps) {
	const { t } = useTranslation();

	const templatesQuery = useAgentTemplates();
	const importMutation = useImportAgentTemplates();

	const [selected, setSelected] = useState<readonly string[]>([]);

	const templates = useMemo(() => templatesQuery.data ?? [], [templatesQuery.data]);
	const grouped = useMemo(() => groupByDivision(templates), [templates]);

	const toggle = useCallback((slug: string, checked: boolean) => {
		setSelected((current) => (checked ? [...current, slug] : current.filter((selectedSlug) => selectedSlug !== slug)));
	}, []);

	const handleImport = useCallback(() => {
		if (selected.length === 0) {
			return;
		}
		importMutation.mutate(
			{ body: { slugs: [...selected] } },
			{
				onSuccess: (result) => {
					toast.success(
						t("pages.agents.templates.importSuccess", "{{imported}} added, {{skipped}} skipped.", {
							imported: result?.imported?.length ?? 0,
							skipped: result?.skippedExisting?.length ?? 0,
						}),
					);
					setSelected([]);
				},
			},
		);
	}, [importMutation, selected, t]);

	const footer = (
		<>
			<Button variant="default" onClick={onClose}>
				{t("common.cancel", "Cancel")}
			</Button>
			<Button
				onClick={handleImport}
				loading={importMutation.isPending}
				disabled={selected.length === 0}
				data-testid="agent-template-import-button"
			>
				{t("pages.agents.templates.importButton", "Import selected ({{count}})", { count: selected.length })}
			</Button>
		</>
	);

	return (
		<DialogShell
			opened={opened}
			onClose={onClose}
			size="56rem"
			title={t("pages.agents.templates.title", "Add starter agents")}
			footer={footer}
			data-testid="agent-template-gallery"
		>
			<Stack gap="md">
				<Text c="dimmed" size="sm">
					{t(
						"pages.agents.templates.subtitle",
						"Import curated starter personas as editable agent definitions. They land with no tools — grant tools after import.",
					)}
				</Text>

				{templatesQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.agents.templates.loading", "Loading starter agents…")}</Text>
					</Group>
				) : null}

				{templatesQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-template-error">
						{errorMessage(templatesQuery.error, t("pages.agents.templates.loadError", "Could not load starter agents."))}
					</Alert>
				) : null}

				{importMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-template-import-error">
						{errorMessage(importMutation.error, t("pages.agents.templates.importError", "Could not import the selected agents."))}
					</Alert>
				) : null}

				{!templatesQuery.isLoading && !templatesQuery.error && templates.length === 0 ? (
					<Text c="dimmed" data-testid="agent-template-empty">
						{t("pages.agents.templates.empty", "No starter agents are available.")}
					</Text>
				) : null}

				{grouped.map(([division, divisionTemplates]) => (
					<Stack key={division} gap="xs" data-testid={`agent-template-division-${division}`}>
						<Title order={5} tt="capitalize">
							{division}
						</Title>
						{divisionTemplates.map((template) => {
							const slug = template.slug ?? "";
							const estimate = template.estimatedPromptTokens ?? 0;
							const overBudget = isOverTokenBudget(estimate);
							const alreadyImported = template.alreadyImported ?? false;
							const tokenLabel = t("pages.agents.templates.tokenBadge", "~{{count}} tokens", { count: estimate });
							const tokenBadge = (
								<Badge
									variant={overBudget ? "filled" : "light"}
									color={overBudget ? "yellow" : "gray"}
									style={{ flexShrink: 0 }}
									data-testid={`agent-template-token-${slug}`}
								>
									{tokenLabel}
								</Badge>
							);

							return (
								<Group
									key={slug}
									justify="space-between"
									align="flex-start"
									wrap="nowrap"
									data-testid={`agent-template-row-${slug}`}
								>
									<Checkbox
										checked={selected.includes(slug)}
										disabled={alreadyImported || importMutation.isPending}
										onChange={(event) => toggle(slug, event.currentTarget.checked)}
										data-testid={`agent-template-checkbox-${slug}`}
										style={{ flex: 1, minWidth: 0 }}
										label={
											<Stack gap={2}>
												<Group gap="xs" align="center">
													<Text fw={600}>{template.name}</Text>
													{alreadyImported ? (
														<Badge variant="light" color="green" size="sm">
															{t("pages.agents.templates.alreadyImported", "Imported")}
														</Badge>
													) : null}
												</Group>
												{template.description ? (
													<Text size="xs" c="dimmed">
														{template.description}
													</Text>
												) : null}
											</Stack>
										}
									/>
									{overBudget ? (
										<Tooltip
											multiline={true}
											w={240}
											label={t(
												"pages.agents.templates.tokenWarning",
												"This persona's estimated prompt is large for local models. Estimate is a chars/4 heuristic.",
											)}
										>
											{tokenBadge}
										</Tooltip>
									) : (
										tokenBadge
									)}
								</Group>
							);
						})}
					</Stack>
				))}
			</Stack>
		</DialogShell>
	);
}
