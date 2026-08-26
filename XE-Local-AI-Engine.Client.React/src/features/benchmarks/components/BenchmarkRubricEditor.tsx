import { ActionIcon, Button, Card, Group, NumberInput, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { BenchmarkVerifierEditor } from "@/features/benchmarks/components/BenchmarkVerifierEditor";
import type { BenchmarkRubric, BenchmarkRubricCriterion, BenchmarkRubricIssue } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkRubricLimits, toBenchmarkCriterionId } from "@/features/benchmarks/models/BenchmarkModels";
import { isVerifiableCriterionKind, toBenchmarkCriterionKind, verifierConfigIssue } from "@/features/benchmarks/models/BenchmarkVerifier";
import type { BenchmarkRubricPresets } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkRubricEditorProps {
	rubric: BenchmarkRubric;
	presets: BenchmarkRubricPresets | undefined;
	/** The first bound the node would reject, surfaced on the offending row once the operator has tried to save. */
	issue: BenchmarkRubricIssue | null;
	onChange: (rubric: BenchmarkRubric) => void;
}

const emptyCriterion: BenchmarkRubricCriterion = { id: "", title: "", description: "", weight: 25, kind: "llm", config: null };

/**
 * The criteria the judge scores against, 0..10 each, rolled up by weight into the run's 0..100 judge score. Bounds
 * mirror the node's validator so a rubric is refused here rather than after a round-trip; the whole editor is one
 * controlled value, because every edit changes the policy hash and therefore what a save would re-judge.
 */
export function BenchmarkRubricEditor({ rubric, presets, issue, onChange }: BenchmarkRubricEditorProps) {
	const { t } = useTranslation();
	const criteria = rubric.criteria;
	const replace = (index: number, patch: Partial<BenchmarkRubricCriterion>): void => {
		onChange({
			...rubric,
			criteria: criteria.map((criterion, position) => (position === index ? { ...criterion, ...patch } : criterion)),
		});
	};
	const issueOn = (index: number, code: BenchmarkRubricIssue["code"]): string | undefined =>
		issue?.index === index && issue.code === code ? t(`pages.benchmarks.rubric.issues.${code}`, "Invalid value.") : undefined;
	const presetOptions: { key: string; label: string; rubric: BenchmarkRubric | null }[] = [
		{ key: "default", label: t("pages.benchmarks.rubric.presetDefault", "Default"), rubric: presets?.default ?? null },
		{
			key: "programming",
			label: t("pages.benchmarks.rubric.presetProgramming", "Programming"),
			rubric: presets?.programming ?? null,
		},
		{ key: "reasoning", label: t("pages.benchmarks.rubric.presetReasoning", "Reasoning"), rubric: presets?.reasoning ?? null },
		{
			key: "verifiable",
			label: t("pages.benchmarks.rubric.presetVerifiable", "Verifiable"),
			rubric: presets?.verifiable ?? null,
		},
		{
			key: "codeExecution",
			label: t("pages.benchmarks.rubric.presetCodeExecution", "Code execution"),
			rubric: presets?.codeExecution ?? null,
		},
	];

	return (
		<Stack gap="sm" data-testid="benchmark-rubric-editor">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={600}>
					{t("pages.benchmarks.rubric.title", "Rubric")}
				</Text>
				<Group gap="xs">
					<Text size="xs" c="dimmed">
						{t("pages.benchmarks.rubric.presets", "Presets")}
					</Text>
					{presetOptions.map((preset) => (
						<Button
							key={preset.key}
							size="compact-xs"
							variant="default"
							disabled={preset.rubric === null}
							onClick={() => preset.rubric && onChange(preset.rubric)}
							data-testid={`benchmark-rubric-preset-${preset.key}`}
						>
							{preset.label}
						</Button>
					))}
				</Group>
			</Group>
			{issue?.index === -1 ? (
				<Text size="xs" c="red">
					{t("pages.benchmarks.rubric.issues.count", "A rubric needs between 1 and 8 criteria.")}
				</Text>
			) : null}
			{criteria.map((criterion, index) => (
				// The criteria are an ordered list the operator edits in place; there is no stable server id to key on
				// while a row is still being typed, so the position is the key.
				// biome-ignore lint/suspicious/noArrayIndexKey: rows are positional until the rubric is saved.
				<Card key={index} withBorder={true} radius="sm" padding="sm" data-testid={`benchmark-rubric-criterion-${index}`}>
					<Stack gap="xs">
						{/*
						  * One line on a wide form, three wrapped fields on a phone: the title claims the first line
						  * once the row can no longer seat all three, and the id shrinks (`miw={0}`, which the weight
						  * deliberately does not do) rather than pushing the delete button off the card.
						  */}
						<Group gap="xs" align="flex-start">
							<TextInput
								flex="1 1 220px"
								miw={0}
								label={t("pages.benchmarks.rubric.criterionTitle", "Title")}
								maxLength={benchmarkRubricLimits.maxTitleLength}
								value={criterion.title}
								error={issueOn(index, "title")}
								onChange={(event) => {
									const title = event.currentTarget.value;
									// The id follows the title until the operator edits it: once the two stop matching, the
									// id is the operator's and a later title edit must not silently rewrite it.
									const follows = criterion.id === toBenchmarkCriterionId(criterion.title);
									replace(index, { title, ...(follows ? { id: toBenchmarkCriterionId(title) } : {}) });
								}}
							/>
							<TextInput
								flex="0 1 180px"
								miw={0}
								label={t("pages.benchmarks.rubric.criterionId", "Id")}
								maxLength={benchmarkRubricLimits.maxIdLength}
								value={criterion.id}
								error={issueOn(index, "id") ?? issueOn(index, "duplicateId")}
								onChange={(event) => {
									const id = event.currentTarget.value;
									replace(index, { id });
								}}
							/>
							<NumberInput
								flex="0 0 110px"
								label={t("pages.benchmarks.rubric.criterionWeight", "Weight")}
								min={benchmarkRubricLimits.minWeight}
								max={benchmarkRubricLimits.maxWeight}
								clampBehavior="strict"
								value={criterion.weight}
								error={issueOn(index, "weight")}
								onChange={(value) => replace(index, { weight: Number(value) || benchmarkRubricLimits.minWeight })}
							/>
							<ActionIcon
								mt={26}
								variant="subtle"
								color="red"
								disabled={criteria.length <= benchmarkRubricLimits.minCriteria}
								aria-label={t("pages.benchmarks.rubric.remove", "Remove criterion")}
								onClick={() => onChange({ ...rubric, criteria: criteria.filter((_, position) => position !== index) })}
								data-testid={`benchmark-rubric-remove-${index}`}
							>
								<IconTrash size={16} />
							</ActionIcon>
						</Group>
						<Textarea
							label={t("pages.benchmarks.rubric.criterionDescription", "What the judge should look for")}
							description={
								isVerifiableCriterionKind(toBenchmarkCriterionKind(criterion.kind))
									? t(
											"pages.benchmarks.rubric.criterionDescriptionVerified",
											"This criterion is decided server-side, so the text is documentation for the operator rather than an instruction to a model.",
										)
									: undefined
							}
							autosize={true}
							minRows={2}
							maxLength={benchmarkRubricLimits.maxDescriptionLength}
							value={criterion.description}
							error={issueOn(index, "description")}
							onChange={(event) => {
								const description = event.currentTarget.value;
								replace(index, { description });
							}}
						/>
						<BenchmarkVerifierEditor
							kind={toBenchmarkCriterionKind(criterion.kind)}
							config={criterion.config ?? null}
							issue={verifierConfigIssue(toBenchmarkCriterionKind(criterion.kind), criterion.config)}
							onChange={(patch) => replace(index, patch)}
							testId={`benchmark-verifier-${index}`}
						/>
					</Stack>
				</Card>
			))}
			<Group>
				<Button
					size="compact-sm"
					variant="light"
					leftSection={<IconPlus size={14} />}
					disabled={criteria.length >= benchmarkRubricLimits.maxCriteria}
					onClick={() => onChange({ ...rubric, criteria: [...criteria, { ...emptyCriterion }] })}
					data-testid="benchmark-rubric-add"
				>
					{t("pages.benchmarks.rubric.add", "Add criterion")}
				</Button>
				<Text size="xs" c="dimmed">
					{t("pages.benchmarks.rubric.count", "{{count}} of {{max}} criteria", {
						count: criteria.length,
						max: benchmarkRubricLimits.maxCriteria,
					})}
				</Text>
			</Group>
		</Stack>
	);
}
