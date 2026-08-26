import { Checkbox, Group, JsonInput, NumberInput, Select, Stack, Switch, TagsInput, Text, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";

import type {
	BenchmarkCriterionKind,
	BenchmarkVerifierConfig,
	BenchmarkVerifierIssue,
} from "@/features/benchmarks/models/BenchmarkVerifier";
import {
	benchmarkConstraintFormats,
	benchmarkCriterionKinds,
	benchmarkPythonExtractModes,
	benchmarkPythonTestsLimits,
	defaultVerifierConfig,
	maxVerifierPatternLength,
	parseVerifierConfig,
	serializeVerifierConfig,
} from "@/features/benchmarks/models/BenchmarkVerifier";

interface BenchmarkVerifierEditorProps {
	kind: BenchmarkCriterionKind;
	/** The criterion's config as stored: JSON, or null for an `llm` criterion. */
	config: string | null;
	/** The pre-check this criterion currently fails, or null. Shown on the field it belongs to. */
	issue: BenchmarkVerifierIssue | null;
	/**
	 * Renders the kind as a fact rather than a choice. A per-ITEM override supplies one criterion's configuration and
	 * nothing else — the policy owns how the criterion is decided, and offering the picker here would imply an item
	 * can change that for itself.
	 */
	lockKind?: boolean;
	onChange: (patch: { kind: BenchmarkCriterionKind; config: string | null }) => void;
	testId: string;
}

const readString = (config: BenchmarkVerifierConfig, key: string): string =>
	typeof config[key] === "string" ? (config[key] as string) : "";
const readNumber = (config: BenchmarkVerifierConfig, key: string): number | "" =>
	typeof config[key] === "number" ? (config[key] as number) : "";
const readList = (config: BenchmarkVerifierConfig, key: string): string[] =>
	Array.isArray(config[key]) ? (config[key] as unknown[]).filter((entry): entry is string => typeof entry === "string") : [];

/**
 * How one rubric criterion is decided, and the configuration that way of deciding needs. `llm` is the default and
 * costs a model turn; every other kind is checked server-side against the graded answer with no inference at all — a
 * rubric whose criteria are ALL verifiable is judged without spawning llama-server.
 *
 * The pre-check here mirrors the node's validator so a mistake is caught while it is being typed. It is not a
 * substitute for that validator: saving a judge policy re-validates everything and its refusal is what the operator
 * sees for anything this misses.
 */
export function BenchmarkVerifierEditor({
	kind,
	config,
	issue,
	lockKind = false,
	onChange,
	testId,
}: BenchmarkVerifierEditorProps) {
	const { t } = useTranslation();
	const parsed = parseVerifierConfig(config);
	const message = (...codes: BenchmarkVerifierIssue[]): string | undefined =>
		issue !== null && codes.includes(issue)
			? t(`pages.benchmarks.verifier.issues.${issue}`, "Invalid configuration.")
			: undefined;
	// Every write goes through here so a field edit can never leave the stored blob as something other than this
	// kind's shape — changing one key rewrites the whole config from the parsed copy.
	const write = (patch: BenchmarkVerifierConfig): void => {
		const next = { ...parsed, ...patch };
		for (const [key, value] of Object.entries(patch)) {
			// An emptied optional field is REMOVED rather than stored as "" or null: the node reads an absent member as
			// "not constrained", and a present empty one as a constraint that nothing can satisfy.
			if (value === undefined || value === "" || (Array.isArray(value) && value.length === 0)) {
				delete next[key];
			}
		}
		onChange({ kind, config: serializeVerifierConfig(next) });
	};

	return (
		<Stack gap="xs" data-testid={testId}>
			{lockKind ? (
				<Text size="xs" c="dimmed" data-testid={`${testId}-kind-locked`}>
					{t("pages.benchmarks.verifier.kindLocked", "Decided by: {{kind}} — set by the judge policy.", {
						kind: t(`pages.benchmarks.verifier.kinds.${kind}`, kind),
					})}
				</Text>
			) : (
				<Select
					w={220}
					label={t("pages.benchmarks.verifier.kind", "Decided by")}
					description={t(
						"pages.benchmarks.verifier.kindHelp",
						"Anything but the model is checked server-side, with no inference.",
					)}
					allowDeselect={false}
					value={kind}
					data={benchmarkCriterionKinds.map((option) => ({
						value: option,
						label: t(`pages.benchmarks.verifier.kinds.${option}`, option),
					}))}
					// Switching kind replaces the config wholesale: a regex pattern is not an expected answer, and carrying
					// the old keys over would send the node a blob that cannot be parsed as the new kind.
					onChange={(value) => {
						const next = benchmarkCriterionKinds.find((option) => option === value) ?? "llm";
						onChange({ kind: next, config: defaultVerifierConfig(next) });
					}}
					data-testid={`${testId}-kind`}
				/>
			)}

			{kind === "exact" ? (
				<Stack gap={4}>
					<TextInput
						label={t("pages.benchmarks.verifier.expected", "Expected answer")}
						value={readString(parsed, "expected")}
						error={message("expectedRequired")}
						onChange={(event) => write({ expected: event.currentTarget.value })}
						data-testid={`${testId}-expected`}
					/>
					<Group gap="md">
						{(["trim", "collapseWhitespace", "caseInsensitive", "stripMarkdown"] as const).map((flag) => {
							const normalize = parseVerifierConfig(JSON.stringify(parsed["normalize"] ?? {}));
							// `trim` is the node's only default-on flag, so an unset value reads as on for it and off for
							// the rest — matching `BenchmarkVerifierNormalizeV1`'s own defaults.
							const checked = typeof normalize[flag] === "boolean" ? (normalize[flag] as boolean) : flag === "trim";
							return (
								<Checkbox
									key={flag}
									size="xs"
									label={t(`pages.benchmarks.verifier.normalize.${flag}`, flag)}
									checked={checked}
									onChange={(event) => write({ normalize: { ...normalize, [flag]: event.currentTarget.checked } })}
									data-testid={`${testId}-normalize-${flag}`}
								/>
							);
						})}
					</Group>
				</Stack>
			) : null}

			{kind === "regex" ? (
				<Stack gap={4}>
					<TextInput
						label={t("pages.benchmarks.verifier.pattern", "Pattern")}
						description={t(
							"pages.benchmarks.verifier.patternHelp",
							"Matched in linear time: lookaround, backreferences and atomic groups are refused when the judge is saved.",
						)}
						maxLength={maxVerifierPatternLength}
						value={readString(parsed, "pattern")}
						error={message("patternRequired", "patternTooLong", "patternInvalid")}
						onChange={(event) => write({ pattern: event.currentTarget.value })}
						data-testid={`${testId}-pattern`}
					/>
					<Switch
						size="xs"
						label={t("pages.benchmarks.verifier.mustMatch", "The answer must match")}
						checked={parsed["mustMatch"] !== false}
						onChange={(event) => write({ mustMatch: event.currentTarget.checked })}
						data-testid={`${testId}-must-match`}
					/>
				</Stack>
			) : null}

			{kind === "jsonSchema" ? (
				<JsonInput
					label={t("pages.benchmarks.verifier.schema", "Schema")}
					description={t("pages.benchmarks.verifier.schemaHelp", "Enforced keywords: {{keywords}}. Anything else is refused.", {
						keywords: "type, properties, required, items, enum, const, additionalProperties",
					})}
					autosize={true}
					minRows={4}
					formatOnBlur={true}
					validationError={t("pages.benchmarks.verifier.issues.schemaInvalidJson", "That is not valid JSON.")}
					value={config ?? ""}
					error={message("schemaRequired", "schemaKeyword")}
					onChange={(value) => onChange({ kind, config: value.trim() === "" ? null : value })}
					data-testid={`${testId}-schema`}
				/>
			) : null}

			{kind === "mathAnswer" ? (
				<Stack gap={4}>
					<TextInput
						label={t("pages.benchmarks.verifier.expectedNumber", "Expected value")}
						description={t("pages.benchmarks.verifier.expectedNumberHelp", "A number or a fraction such as 3/4.")}
						value={typeof parsed["expected"] === "number" ? String(parsed["expected"]) : readString(parsed, "expected")}
						error={message("mathExpected")}
						onChange={(event) => write({ expected: event.currentTarget.value })}
						data-testid={`${testId}-expected-number`}
					/>
					<Group gap="sm" align="flex-start">
						{(["relativeTolerance", "absoluteTolerance"] as const).map((key) => (
							<NumberInput
								key={key}
								w={180}
								min={0}
								step={0.001}
								label={t(`pages.benchmarks.verifier.${key}`, key)}
								value={readNumber(parsed, key)}
								error={message("toleranceInvalid")}
								onChange={(value) => write({ [key]: value === "" ? undefined : Number(value) })}
								data-testid={`${testId}-${key}`}
							/>
						))}
					</Group>
				</Stack>
			) : null}

			{kind === "pythonTests" ? (
				<Stack gap={4} data-testid={`${testId}-python`}>
					<Text size="xs" c="dimmed">
						{t(
							"pages.benchmarks.verifier.pythonTestsHelp",
							"The answer's code runs in an untrusted child process inside the compute sandbox; these tests run in the parent, which never executes it. All tests pass = 10, anything else = 0. The tests reach the answer as candidate.name(...), or as bare names you list under Exports.",
						)}
					</Text>
					<CodeEditor
						value={readString(parsed, "testCode")}
						language="python"
						height={220}
						aria-label={t("pages.benchmarks.verifier.testCode", "Test code")}
						onChange={(value) => write({ testCode: value })}
						data-testid={`${testId}-test-code`}
					/>
					{message("testCodeRequired", "testCodeTooLong") === undefined ? null : (
						<Text size="xs" c="red" data-testid={`${testId}-test-code-error`}>
							{message("testCodeRequired", "testCodeTooLong")}
						</Text>
					)}
					<TagsInput
						label={t("pages.benchmarks.verifier.exports", "Exports")}
						description={t(
							"pages.benchmarks.verifier.exportsHelp",
							"Names the tests may call directly instead of through candidate. Plain Python identifiers, at most {{max}}.",
							{ max: benchmarkPythonTestsLimits.maxExports },
						)}
						value={readList(parsed, "exports")}
						error={message("exportsInvalid", "exportsCap")}
						onChange={(value) => write({ exports: value })}
						data-testid={`${testId}-exports`}
					/>
					<Group gap="sm" align="flex-start">
						<NumberInput
							w={180}
							min={1}
							max={benchmarkPythonTestsLimits.maxTimeoutSeconds}
							allowDecimal={false}
							label={t("pages.benchmarks.verifier.timeoutSeconds", "Timeout (s)")}
							description={t("pages.benchmarks.verifier.timeoutSecondsHelp", "Empty = the node's own compute timeout.")}
							value={readNumber(parsed, "timeoutSeconds")}
							error={message("timeoutRange")}
							onChange={(value) => write({ timeoutSeconds: value === "" ? undefined : Number(value) })}
							data-testid={`${testId}-timeout`}
						/>
						<Select
							w={220}
							clearable={true}
							label={t("pages.benchmarks.verifier.extract", "Code taken from")}
							description={t("pages.benchmarks.verifier.extractHelp", "Empty = the node's default, the first python fence.")}
							value={readString(parsed, "extract") || null}
							data={benchmarkPythonExtractModes.map((mode) => ({
								value: mode,
								label: t(`pages.benchmarks.verifier.extractModes.${mode}`, mode),
							}))}
							error={message("extractInvalid")}
							onChange={(value) => write({ extract: value ?? undefined })}
							data-testid={`${testId}-extract`}
						/>
					</Group>
				</Stack>
			) : null}

			{kind === "constraint" ? (
				<Stack gap={4}>
					<Group gap="sm" align="flex-start">
						{(["minWords", "maxWords"] as const).map((key) => (
							<NumberInput
								key={key}
								w={140}
								min={0}
								allowDecimal={false}
								label={t(`pages.benchmarks.verifier.${key}`, key)}
								value={readNumber(parsed, key)}
								error={message("constraintWords")}
								onChange={(value) => write({ [key]: value === "" ? undefined : Number(value) })}
								data-testid={`${testId}-${key}`}
							/>
						))}
						<Select
							w={200}
							label={t("pages.benchmarks.verifier.format", "Format")}
							clearable={true}
							value={readString(parsed, "format") || null}
							data={benchmarkConstraintFormats.map((format) => ({
								value: format,
								label: t(`pages.benchmarks.verifier.formats.${format}`, format),
							}))}
							error={message("constraintFormat")}
							onChange={(value) => write({ format: value ?? undefined })}
							data-testid={`${testId}-format`}
						/>
					</Group>
					{(["mustContain", "mustNotContain"] as const).map((key) => (
						<TagsInput
							key={key}
							label={t(`pages.benchmarks.verifier.${key}`, key)}
							value={readList(parsed, key)}
							error={message("constraintContains")}
							onChange={(value) => write({ [key]: value })}
							data-testid={`${testId}-${key}`}
						/>
					))}
					{message("constraintEmpty") === undefined ? null : (
						<Text size="xs" c="red" data-testid={`${testId}-constraint-empty`}>
							{message("constraintEmpty")}
						</Text>
					)}
				</Stack>
			) : null}
		</Stack>
	);
}
