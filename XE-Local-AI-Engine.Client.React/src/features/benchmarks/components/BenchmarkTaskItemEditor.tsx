import { Alert, Button, Divider, Group, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlus } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { TaskItemCards } from "@/features/benchmarks/components/BenchmarkTaskItemEditor/TaskItemCards";
import { TaskItemForm } from "@/features/benchmarks/components/BenchmarkTaskItemEditor/TaskItemForm";
import type { BenchmarkRubricCriterion } from "@/features/benchmarks/models/BenchmarkModels";
import { mutationFailure, otherLeafCount } from "@/features/benchmarks/models/BenchmarkTaskItemEditorHelpers";
import type { BenchmarkTaskItem, BenchmarkTaskItemDraft } from "@/features/benchmarks/models/BenchmarkTaskItems";
import {
	benchmarkTaskItemChildren,
	benchmarkTaskItemGroups,
	benchmarkTaskItemLimits,
	emptyBenchmarkTaskItemDraft,
	leafBenchmarkTaskItems,
	niahGeneratorIssue,
	parseNiahGeneratorConfig,
	reorderBenchmarkTaskItems,
	serializeNiahGeneratorConfig,
	toBenchmarkTaskItemDraft,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import { parseVerifierConfig } from "@/features/benchmarks/models/BenchmarkVerifier";
import {
	useBenchmarkTaskItems,
	useCreateBenchmarkTaskItem,
	useDeleteBenchmarkTaskItem,
	useReorderBenchmarkTaskItems,
	useUpdateBenchmarkTaskItem,
} from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkTaskItemEditorProps {
	projectId: string;
	/** The project's frozen window. A long-context probe longer than it is refused here and again at freeze. */
	projectContextTokens: number;
	/** The project has runs, so every edit below unranks something. Says what, before the operator clicks. */
	hasRuns: boolean;
	/** The judge policy's criteria. Only the verifiable ones can be overridden per item — an `llm` one has no config. */
	criteria: readonly BenchmarkRubricCriterion[];
}

/**
 * The project's task items: add, edit, reorder, delete, and per-item overrides of the judge policy's verifier config.
 *
 * Every mutation here changes what the project's score MEANS, and the alerts say which one costs what: editing an item
 * unranks the cells that answered it (`item-revised`), adding or deleting one unranks every cell measured under the
 * old set (`item-set-revised`), and reordering costs nothing at all — the item-set hash is taken over ids, not
 * positions.
 */
export function BenchmarkTaskItemEditor({ projectId, projectContextTokens, hasRuns, criteria }: BenchmarkTaskItemEditorProps) {
	const { t } = useTranslation();
	const itemsQuery = useBenchmarkTaskItems(projectId);
	const createItem = useCreateBenchmarkTaskItem();
	const updateItem = useUpdateBenchmarkTaskItem();
	const deleteItem = useDeleteBenchmarkTaskItem();
	const reorderItems = useReorderBenchmarkTaskItems();
	// `null` = nothing open, `"new"` = the add form, otherwise the id being edited.
	const [editing, setEditing] = useState<string | null>(null);
	const [draft, setDraft] = useState<BenchmarkTaskItemDraft>(emptyBenchmarkTaskItemDraft);
	const [attempted, setAttempted] = useState(false);
	const [expanded, setExpanded] = useState(() => new Set<string>());
	const [pendingDelete, setPendingDelete] = useState<BenchmarkTaskItem | null>(null);

	const items = itemsQuery.data?.items ?? [];
	const groups = benchmarkTaskItemGroups(items);
	const leafCount = leafBenchmarkTaskItems(items).length;
	const isBusy = createItem.isPending || updateItem.isPending || deleteItem.isPending || reorderItems.isPending;
	const niah = parseNiahGeneratorConfig(draft.generatorConfig);
	const itemsById = new Map(items.map((item) => [item.id, item]));
	const editingItem = editing === null || editing === "new" ? null : (itemsById.get(editing) ?? null);
	const niahIssue =
		draft.kind === "niah" ? niahGeneratorIssue(niah, projectContextTokens, otherLeafCount(items, leafCount, editingItem)) : null;
	const promptRequired = draft.prompt.trim().length === 0;

	const open = (item: BenchmarkTaskItem | null): void => {
		setAttempted(false);
		setEditing(item === null ? "new" : item.id);
		setDraft(item === null ? emptyBenchmarkTaskItemDraft() : toBenchmarkTaskItemDraft(item));
	};
	const close = (): void => {
		setEditing(null);
		setAttempted(false);
	};
	const writeNiah = (patch: Partial<ReturnType<typeof parseNiahGeneratorConfig>>): void =>
		setDraft((current) => ({ ...current, generatorConfig: serializeNiahGeneratorConfig({ ...niah, ...patch }) }));

	const save = (): void => {
		setAttempted(true);
		if (promptRequired || niahIssue !== null) {
			return;
		}
		if (editingItem === null) {
			createItem.mutate(
				{ projectId, expectedProjectVersion: itemsQuery.data?.projectVersion ?? 0, draft },
				{
					onSuccess: close,
					onError: mutationFailure(t("pages.benchmarks.items.errors.create", "Could not add this task item.")),
				},
			);
			return;
		}
		updateItem.mutate(
			{ projectId, item: editingItem, draft },
			{ onSuccess: close, onError: mutationFailure(t("pages.benchmarks.items.errors.update", "Could not save this task item.")) },
		);
	};
	const move = (item: BenchmarkTaskItem, direction: -1 | 1): void =>
		reorderItems.mutate(
			{ projectId, itemIds: reorderBenchmarkTaskItems(items, item.id, direction) },
			{ onError: mutationFailure(t("pages.benchmarks.items.errors.reorder", "Could not reorder the task items.")) },
		);

	// A verifier override is one criterion's CONFIG, keyed by criterion id. Emptying it removes the key, which is how
	// the item goes back to the policy's own configuration for that criterion.
	const writeOverride = (criterionId: string, config: string | null): void =>
		setDraft((current) => {
			const next = { ...(current.verifierConfig ?? {}) };
			const parsed = config === null ? {} : parseVerifierConfig(config);
			if (Object.keys(parsed).length === 0) {
				delete next[criterionId];
			} else {
				next[criterionId] = parsed;
			}
			return { ...current, verifierConfig: Object.keys(next).length === 0 ? null : next };
		});

	return (
		<Stack gap="sm" data-testid="benchmark-task-items">
			<Divider
				label={t("pages.benchmarks.items.section", "Task items")}
				labelPosition="left"
				data-testid="benchmark-items-section"
			/>
			<Group justify="space-between" align="center">
				<Text size="sm" c="dimmed" data-testid="benchmark-items-count">
					{t("pages.benchmarks.items.count", "{{count}} of {{max}} items — a model's project score is the mean over them", {
						count: leafCount,
						max: benchmarkTaskItemLimits.maxLeafItems,
					})}
				</Text>
				<Button
					variant="default"
					size="xs"
					leftSection={<IconPlus size={14} />}
					disabled={isBusy || leafCount >= benchmarkTaskItemLimits.maxLeafItems}
					onClick={() => open(null)}
					data-testid="benchmark-item-add"
				>
					{t("pages.benchmarks.items.add", "Add item")}
				</Button>
			</Group>

			{hasRuns ? (
				<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-items-history-warning">
					{t(
						"pages.benchmarks.items.historyWarning",
						"This project has runs. Editing an item unranks every measurement of it (item-revised); adding or deleting one unranks every combination measured under the old set (item-set-revised). Reordering changes nothing — the set is identified by its items, not their positions.",
					)}
				</Alert>
			) : null}

			<TaskItemCards
				groups={groups}
				expanded={expanded}
				isBusy={isBusy}
				onToggleExpanded={(itemId) =>
					setExpanded((current) => {
						const next = new Set(current);
						if (next.has(itemId)) {
							next.delete(itemId);
						} else {
							next.add(itemId);
						}
						return next;
					})
				}
				onMove={move}
				onOpen={open}
				onDelete={setPendingDelete}
			/>

			<TaskItemForm
				editing={editing}
				editingItem={editingItem}
				hasRuns={hasRuns}
				draft={draft}
				setDraft={setDraft}
				attempted={attempted}
				promptRequired={promptRequired}
				niah={niah}
				niahIssue={niahIssue}
				projectContextTokens={projectContextTokens}
				criteria={criteria}
				isSaving={createItem.isPending || updateItem.isPending}
				onWriteNiah={writeNiah}
				onOverride={writeOverride}
				onClose={close}
				onSave={save}
			/>

			<DialogShell
				opened={pendingDelete !== null}
				onClose={() => setPendingDelete(null)}
				title={t("pages.benchmarks.items.deleteTitle", "Delete this task item?")}
				size="md"
				data-testid="benchmark-item-delete-confirm"
			>
				<Stack gap="md">
					<Text>
						{t(
							"pages.benchmarks.items.deleteConfirm",
							"Deleting an item changes what the project measures. Every combination that was already measured is excluded as item-set-revised — it was scored against a suite this project no longer has, and a partial one never becomes complete by losing the question it missed.",
						)}
					</Text>
					{pendingDelete?.kind === "niah" ? (
						<Text size="sm" c="dimmed">
							{t("pages.benchmarks.items.deleteCases", "Its {{count}} generated probe cases are deleted with it.", {
								count: benchmarkTaskItemChildren(items, pendingDelete.id).length,
							})}
						</Text>
					) : null}
					<Group justify="flex-end">
						<Button variant="default" onClick={() => setPendingDelete(null)}>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							color="red"
							loading={deleteItem.isPending}
							onClick={() => {
								const target = pendingDelete;
								setPendingDelete(null);
								if (target) {
									deleteItem.mutate(
										{ projectId, item: target },
										{ onError: mutationFailure(t("pages.benchmarks.items.errors.delete", "Could not delete this task item.")) },
									);
								}
							}}
							data-testid="benchmark-item-delete-accept"
						>
							{t("common.delete", "Delete")}
						</Button>
					</Group>
				</Stack>
			</DialogShell>
		</Stack>
	);
}
