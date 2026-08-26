import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Card,
	Collapse,
	Divider,
	Group,
	NumberInput,
	Stack,
	Switch,
	Text,
	Textarea,
	TextInput,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconChevronDown,
	IconChevronRight,
	IconChevronUp,
	IconPencil,
	IconPlus,
	IconTrash,
} from "@tabler/icons-react";
import { type Dispatch, type SetStateAction, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { toast } from "@/core/ui/notifications/Toast";
import { BenchmarkVerifierEditor } from "@/features/benchmarks/components/BenchmarkVerifierEditor";
import type { BenchmarkRubricCriterion } from "@/features/benchmarks/models/BenchmarkModels";
import type {
	BenchmarkTaskItem,
	BenchmarkTaskItemDraft,
	BenchmarkTaskItemKind,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import {
	benchmarkNiahCaseLabel,
	benchmarkTaskItemChildren,
	benchmarkTaskItemGroups,
	benchmarkTaskItemLimits,
	emptyBenchmarkTaskItemDraft,
	leafBenchmarkTaskItems,
	niahCaseCount,
	niahGeneratorIssue,
	parseNiahGeneratorConfig,
	reorderBenchmarkTaskItems,
	serializeNiahGeneratorConfig,
	toBenchmarkTaskItemDraft,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import {
	parseVerifierConfig,
	serializeVerifierConfig,
	toBenchmarkCriterionKind,
} from "@/features/benchmarks/models/BenchmarkVerifier";
import type { BenchmarkVerifierConfig } from "@/features/benchmarks/models/BenchmarkVerifier";
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

/** A numeric axis typed as text: `8192, 32768`. Kept as a string while editing so a half-typed number is not eaten. */
const parseAxis = (value: string): number[] =>
	value
		.split(/[,\s]+/)
		.map((entry) => Number(entry))
		.filter((entry) => Number.isFinite(entry) && entry > 0);
const formatAxis = (values: readonly number[]): string => values.join(", ");

function otherLeafCount(items: readonly BenchmarkTaskItem[], leafCount: number, item: BenchmarkTaskItem | null): number {
	return item === null ? leafCount : leafCount - (item.kind === "niah" ? benchmarkTaskItemChildren(items, item.id).length : 1);
}

const mutationFailure = (fallback: string) => (error: unknown) => toast.error(apiErrorMessage(error, fallback));
const verifierOverride = (draft: BenchmarkTaskItemDraft, criterionId: string): BenchmarkVerifierConfig | null =>
	draft.verifierConfig?.[criterionId] ?? null;

interface TaskItemCardsProps {
	groups: BenchmarkTaskItem[][];
	expanded: ReadonlySet<string>;
	isBusy: boolean;
	onToggleExpanded: (itemId: string) => void;
	onMove: (item: BenchmarkTaskItem, direction: -1 | 1) => void;
	onOpen: (item: BenchmarkTaskItem) => void;
	onDelete: (item: BenchmarkTaskItem) => void;
}

function TaskItemCards({ groups, expanded, isBusy, onToggleExpanded, onMove, onOpen, onDelete }: TaskItemCardsProps) {
	const { t } = useTranslation();
	return (
		<>
			{groups.map(([item, ...cases], groupIndex) => {
				const parent = item as BenchmarkTaskItem;
				const isOpen = expanded.has(parent.id);
				return (
					<Card key={parent.id} withBorder={true} padding="xs" data-testid={`benchmark-item-${parent.id}`}>
						<Group justify="space-between" wrap="nowrap" align="flex-start">
							<Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
								<Badge variant="light">{groupIndex + 1}</Badge>
								<Stack gap={2} style={{ minWidth: 0 }}>
									<Text size="sm" truncate="end" data-testid={`benchmark-item-prompt-${parent.id}`}>
										{parent.prompt}
									</Text>
									<Group gap={4} wrap="nowrap">
										{parent.kind === "niah" ? (
											<StatusBadge
												color="grape"
												label={t("pages.benchmarks.items.caseCount", "{{count}} probe cases", { count: cases.length })}
												data-testid={`benchmark-item-cases-${parent.id}`}
											/>
										) : null}
										{parent.countsTowardScore ? null : (
											<StatusBadge
												color="gray"
												label={t("pages.benchmarks.items.notScored", "own axis")}
												data-testid={`benchmark-item-unscored-${parent.id}`}
											/>
										)}
										{parent.verifierConfig === null ? null : (
											<StatusBadge
												color="blue"
												label={t("pages.benchmarks.items.hasOverride", "verifier override")}
												data-testid={`benchmark-item-override-${parent.id}`}
											/>
										)}
										<Text size="xs" c="dimmed">
											{t("pages.benchmarks.items.revision", "r{{revision}}", { revision: parent.revision })}
										</Text>
									</Group>
								</Stack>
							</Group>
							<Group gap={2} wrap="nowrap">
								{cases.length > 0 ? (
									<ActionIcon
										variant="subtle"
										size="sm"
										aria-label={t("pages.benchmarks.items.showCases", "Show the generated probe cases")}
										aria-expanded={isOpen}
										onClick={() => onToggleExpanded(parent.id)}
										data-testid={`benchmark-item-cases-toggle-${parent.id}`}
									>
										{isOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
									</ActionIcon>
								) : null}
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy || groupIndex === 0}
									aria-label={t("pages.benchmarks.items.moveUp", "Move up")}
									onClick={() => onMove(parent, -1)}
									data-testid={`benchmark-item-up-${parent.id}`}
								>
									<IconChevronUp size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy || groupIndex === groups.length - 1}
									aria-label={t("pages.benchmarks.items.moveDown", "Move down")}
									onClick={() => onMove(parent, 1)}
									data-testid={`benchmark-item-down-${parent.id}`}
								>
									<IconChevronDown size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									disabled={isBusy}
									aria-label={t("common.edit", "Edit")}
									onClick={() => onOpen(parent)}
									data-testid={`benchmark-item-edit-${parent.id}`}
								>
									<IconPencil size={14} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									size="sm"
									color="red"
									// A project always holds at least one item; the node refuses the last one anyway.
									disabled={isBusy || groups.length <= 1}
									aria-label={t("common.delete", "Delete")}
									onClick={() => onDelete(parent)}
									data-testid={`benchmark-item-delete-${parent.id}`}
								>
									<IconTrash size={14} />
								</ActionIcon>
							</Group>
						</Group>
						{cases.length > 0 ? (
							<Collapse expanded={isOpen}>
								<Stack gap={2} mt="xs" pl="md">
									{cases.map((generated) => (
										<Text key={generated.id} size="xs" c="dimmed" data-testid={`benchmark-item-case-${generated.id}`}>
											{benchmarkNiahCaseLabel(generated) ?? generated.prompt.slice(0, 60)}
										</Text>
									))}
								</Stack>
							</Collapse>
						) : null}
					</Card>
				);
			})}
		</>
	);
}

interface TaskItemFormProps {
	editing: string | null;
	editingItem: BenchmarkTaskItem | null;
	hasRuns: boolean;
	draft: BenchmarkTaskItemDraft;
	setDraft: Dispatch<SetStateAction<BenchmarkTaskItemDraft>>;
	attempted: boolean;
	promptRequired: boolean;
	niah: ReturnType<typeof parseNiahGeneratorConfig>;
	niahIssue: ReturnType<typeof niahGeneratorIssue>;
	projectContextTokens: number;
	criteria: readonly BenchmarkRubricCriterion[];
	isSaving: boolean;
	onWriteNiah: (patch: Partial<ReturnType<typeof parseNiahGeneratorConfig>>) => void;
	onOverride: (criterionId: string, config: string | null) => void;
	onClose: () => void;
	onSave: () => void;
}

function TaskItemForm({
	editing,
	editingItem,
	hasRuns,
	draft,
	setDraft,
	attempted,
	promptRequired,
	niah,
	niahIssue,
	projectContextTokens,
	criteria,
	isSaving,
	onWriteNiah: writeNiah,
	onOverride: writeOverride,
	onClose: close,
	onSave: save,
}: TaskItemFormProps) {
	const { t } = useTranslation();
	return (
		<>
			{editing === null ? null : (
				<Card withBorder={true} padding="sm" data-testid="benchmark-item-form">
					<Stack gap="sm">
						<Group gap="xs">
							<Text fw={600} size="sm">
								{editingItem === null
									? t("pages.benchmarks.items.newTitle", "New task item")
									: t("pages.benchmarks.items.editTitle", "Task item {{index}}", { index: editingItem.index + 1 })}
							</Text>
							{/* Offered on ADD only: turning an authored prompt into a generator would delete the answers to it
							    and expand a different set of questions in their place. */}
							{editingItem === null ? (
								<Switch
									size="xs"
									checked={draft.kind === "niah"}
									label={t("pages.benchmarks.items.niahKind", "Long-context probe (NIAH)")}
									onChange={(event) =>
										setDraft(
											emptyBenchmarkTaskItemDraft((event.currentTarget.checked ? "niah" : "prompt") as BenchmarkTaskItemKind),
										)
									}
									data-testid="benchmark-item-kind"
								/>
							) : null}
						</Group>

						{editingItem !== null && hasRuns ? (
							<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-item-revise-warning">
								{t(
									"pages.benchmarks.items.reviseWarning",
									"Saving bumps this item to r{{revision}}. Every run that answered r{{current}} of it is excluded as item-revised — the answers were given to a different question.",
									{ revision: editingItem.revision + 1, current: editingItem.revision },
								)}
							</Alert>
						) : null}

						<Textarea
							label={
								draft.kind === "niah"
									? t("pages.benchmarks.items.niahPrompt", "Probe description")
									: t("pages.benchmarks.items.prompt", "Prompt")
							}
							description={
								draft.kind === "niah"
									? t(
											"pages.benchmarks.items.niahPromptHelp",
											"Shown in the item list. The cases carry their own generated prompts.",
										)
									: undefined
							}
							required={true}
							autosize={true}
							minRows={3}
							value={draft.prompt}
							error={
								attempted && promptRequired ? t("pages.benchmarks.items.validation.prompt", "A prompt is required.") : undefined
							}
							onChange={(event) => {
								const value = event.currentTarget.value;
								setDraft((current) => ({ ...current, prompt: value }));
							}}
							data-testid="benchmark-item-prompt-input"
						/>

						{draft.kind === "niah" ? (
							<Stack gap="xs" data-testid="benchmark-item-niah">
								<Group grow={true} align="flex-start">
									<TextInput
										label={t("pages.benchmarks.items.niahContextTokens", "Probe lengths (tokens)")}
										description={t(
											"pages.benchmarks.items.niahContextTokensHelp",
											"Comma separated. Each must fit the project's {{context}}-token window.",
											{
												context: projectContextTokens,
											},
										)}
										value={formatAxis(niah.contextTokens)}
										onChange={(event) => writeNiah({ contextTokens: parseAxis(event.currentTarget.value) })}
										data-testid="benchmark-item-niah-context"
									/>
									<TextInput
										label={t("pages.benchmarks.items.niahDepths", "Needle depths (%)")}
										description={t(
											"pages.benchmarks.items.niahDepthsHelp",
											"Comma separated, 0..100 — where in the haystack the needle is hidden.",
										)}
										value={formatAxis(niah.needleDepthPercent)}
										onChange={(event) => writeNiah({ needleDepthPercent: parseAxis(event.currentTarget.value) })}
										data-testid="benchmark-item-niah-depths"
									/>
								</Group>
								<TextInput
									label={t("pages.benchmarks.items.niahNeedle", "Needle template")}
									description={t("pages.benchmarks.items.niahNeedleHelp", "Must contain {city} and {code}.")}
									value={niah.needleTemplate}
									onChange={(event) => writeNiah({ needleTemplate: event.currentTarget.value })}
									data-testid="benchmark-item-niah-needle"
								/>
								<TextInput
									label={t("pages.benchmarks.items.niahQuestion", "Question template")}
									description={t("pages.benchmarks.items.niahQuestionHelp", "Must contain {city}.")}
									value={niah.questionTemplate}
									onChange={(event) => writeNiah({ questionTemplate: event.currentTarget.value })}
									data-testid="benchmark-item-niah-question"
								/>
								<Group grow={true} align="flex-start">
									<TextInput
										label={t("pages.benchmarks.items.niahCriterion", "Criterion to override")}
										description={t(
											"pages.benchmarks.items.niahCriterionHelp",
											"An `exact` criterion of the judge policy. Each case supplies its own passcode as that criterion's expected answer.",
										)}
										value={niah.criterionId}
										onChange={(event) => writeNiah({ criterionId: event.currentTarget.value })}
										data-testid="benchmark-item-niah-criterion"
									/>
									<NumberInput
										label={t("pages.benchmarks.items.niahSeed", "Seed")}
										allowDecimal={false}
										value={niah.seed}
										onChange={(value) => writeNiah({ seed: typeof value === "number" ? value : 0 })}
										data-testid="benchmark-item-niah-seed"
									/>
								</Group>
								<Text size="sm" c={niahIssue === null ? "dimmed" : "red"} data-testid="benchmark-item-niah-summary">
									{niahIssue === null
										? t(
												"pages.benchmarks.items.niahSummary",
												"Expands into {{count}} cases — {{count}} runs per combination, and {{count}} against the item cap.",
												{
													count: niahCaseCount(niah),
												},
											)
										: t(`pages.benchmarks.items.niahIssues.${niahIssue}`, "That long-context probe is not valid.", {
												context: projectContextTokens,
												max: benchmarkTaskItemLimits.maxLeafItems,
											})}
								</Text>
							</Stack>
						) : (
							<Textarea
								label={t("pages.benchmarks.items.referenceAnswer", "Reference answer (optional)")}
								description={t(
									"pages.benchmarks.items.referenceAnswerHelp",
									"Overrides the judge policy's reference answer for this item only.",
								)}
								autosize={true}
								minRows={2}
								value={draft.referenceAnswer ?? ""}
								onChange={(event) => {
									const value = event.currentTarget.value;
									setDraft((current) => ({ ...current, referenceAnswer: value.length > 0 ? value : null }));
								}}
								data-testid="benchmark-item-reference"
							/>
						)}

						<Switch
							size="sm"
							checked={draft.kind === "niah" ? niah.countsTowardScore : draft.countsTowardScore}
							label={t("pages.benchmarks.items.countsTowardScore", "Counts toward the project score")}
							description={t(
								"pages.benchmarks.items.countsTowardScoreHelp",
								"Off = measured and reported on its own axis, never averaged into the rubric mean. A long-context probe starts off, because recall is a capability rather than answer quality.",
							)}
							onChange={(event) => {
								const checked = event.currentTarget.checked;
								// A generator is not a run target; its CASES are, so the flag its cases inherit lives in the
								// generator configuration. Both are written so the two never disagree.
								setDraft((current) => ({
									...current,
									countsTowardScore: checked,
									...(current.kind === "niah"
										? { generatorConfig: serializeNiahGeneratorConfig({ ...niah, countsTowardScore: checked }) }
										: {}),
								}));
							}}
							data-testid="benchmark-item-counts"
						/>

						{criteria.length === 0 ? null : (
							<Stack gap="xs">
								<Text size="sm" fw={600}>
									{t("pages.benchmarks.items.overrides", "Verifier overrides")}
								</Text>
								<Text size="xs" c="dimmed">
									{t(
										"pages.benchmarks.items.overridesHelp",
										"Give one criterion a different expected answer for this item. Left empty, the judge policy's own configuration is used.",
									)}
								</Text>
								{criteria.map((criterion) => (
									<Card key={criterion.id} withBorder={true} padding="xs">
										<Stack gap={4}>
											<Text size="xs" fw={600}>
												{criterion.title}
											</Text>
											<BenchmarkVerifierEditor
												kind={toBenchmarkCriterionKind(criterion.kind)}
												config={serializeVerifierConfig(verifierOverride(draft, criterion.id) ?? {})}
												issue={null}
												lockKind={true}
												onChange={(patch) => writeOverride(criterion.id, patch.config)}
												testId={`benchmark-item-override-${criterion.id}`}
											/>
										</Stack>
									</Card>
								))}
							</Stack>
						)}

						<Group justify="flex-end">
							<Button variant="default" size="xs" onClick={close}>
								{t("common.cancel", "Cancel")}
							</Button>
							<Button size="xs" loading={isSaving} onClick={save} data-testid="benchmark-item-save">
								{t("common.save", "Save")}
							</Button>
						</Group>
					</Stack>
				</Card>
			)}
		</>
	);
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
				{ onSuccess: close, onError: mutationFailure(t("pages.benchmarks.items.errors.create", "Could not add this task item.")) },
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
