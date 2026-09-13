import { Alert, Button, Card, Group, NumberInput, Stack, Switch, Text, Textarea, TextInput } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";

import { BenchmarkVerifierEditor } from "@/features/benchmarks/components/BenchmarkVerifierEditor";
import type { BenchmarkRubricCriterion } from "@/features/benchmarks/models/BenchmarkModels";
import { formatAxis, parseAxis, verifierOverride } from "@/features/benchmarks/models/BenchmarkTaskItemEditorHelpers";
import type {
	BenchmarkTaskItem,
	BenchmarkTaskItemDraft,
	BenchmarkTaskItemKind,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import {
	benchmarkTaskItemLimits,
	emptyBenchmarkTaskItemDraft,
	niahCaseCount,
	type niahGeneratorIssue,
	type parseNiahGeneratorConfig,
	serializeNiahGeneratorConfig,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import { serializeVerifierConfig, toBenchmarkCriterionKind } from "@/features/benchmarks/models/BenchmarkVerifier";

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

/** The add/edit form for one task item: the prompt (or the long-context generator's axes) plus its verifier overrides. */
export function TaskItemForm({
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
											"An exact-match criterion of the judge policy. Each case supplies its own passcode as that criterion's expected answer.",
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
