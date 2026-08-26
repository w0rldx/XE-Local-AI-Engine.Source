// How a rubric criterion is decided, and the configuration each way of deciding needs. Mirrors
// `BenchmarkJudgeVerifierConfig.Parse` on the node so an operator is told what is wrong while they are typing it,
// rather than by a 400 after a save that also costs a forced re-judge. The node stays the authority: everything here
// is a pre-check, never a substitute for its validation.

/**
 * `llm` is what every criterion written before P2 carries and the only kind that costs a model turn; every other kind
 * is checked server-side against the graded answer with no inference at all.
 */
export const benchmarkCriterionKinds = ["llm", "exact", "regex", "jsonSchema", "mathAnswer", "constraint"] as const;
export type BenchmarkCriterionKind = (typeof benchmarkCriterionKinds)[number];

/** An absent or unrecognized kind reads as `llm` — the node's own default for a criterion written before P2. */
export const toBenchmarkCriterionKind = (value: unknown): BenchmarkCriterionKind =>
	benchmarkCriterionKinds.find((kind) => kind === value) ?? "llm";

/** Decided by a verifier rather than by a model. */
export const isVerifiableCriterionKind = (kind: BenchmarkCriterionKind): boolean => kind !== "llm";

/** The node's cap. A pattern above it is refused at activation. */
export const maxVerifierPatternLength = 512;

/** The structural JSON-Schema subset the node enforces. A schema naming anything else is refused rather than under-checked. */
export const supportedSchemaKeywords = ["type", "properties", "required", "items", "enum", "const", "additionalProperties"];

export const benchmarkConstraintFormats = ["json", "markdownList", "noMarkdown"] as const;
export type BenchmarkConstraintFormat = (typeof benchmarkConstraintFormats)[number];

/** A criterion's config as an editable object. Opaque on the wire (JSON), so the editor parses and re-serializes it. */
export type BenchmarkVerifierConfig = Record<string, unknown>;

/** A malformed stored config reads as empty rather than throwing — the editor must still open so it can be fixed. */
export function parseVerifierConfig(config: string | null | undefined): BenchmarkVerifierConfig {
	if (!config) {
		return {};
	}
	try {
		const parsed: unknown = JSON.parse(config);
		return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? (parsed as BenchmarkVerifierConfig) : {};
	} catch {
		return {};
	}
}

/** `null` for an empty object: an `llm` criterion must carry no config at all, and `{}` is not the same as absent. */
export const serializeVerifierConfig = (config: BenchmarkVerifierConfig): string | null =>
	Object.keys(config).length === 0 ? null : JSON.stringify(config);

/**
 * What a criterion starts with when its kind is chosen. Deliberately the smallest config the node ACCEPTS is not
 * offered — an empty `exact` has no expected answer and is refused — so these start invalid on purpose and the
 * editor says what is missing. Filling a plausible default in would ship a criterion the operator never read.
 */
export function defaultVerifierConfig(kind: BenchmarkCriterionKind): string | null {
	switch (kind) {
		case "llm":
			return null;
		case "regex":
			return JSON.stringify({ pattern: "", mustMatch: true });
		case "jsonSchema":
			return JSON.stringify({ schema: { type: "object" } });
		case "mathAnswer":
			return JSON.stringify({ expected: "" });
		case "constraint":
			return JSON.stringify({});
		default:
			return JSON.stringify({ expected: "" });
	}
}

/**
 * Why the node would refuse this config, as an i18n key suffix, or null when the pre-check passes. Never a sentence:
 * the message is the caller's to translate, and the node's own sentence is what surfaces if this misses something.
 */
export type BenchmarkVerifierIssue =
	| "llmConfig"
	| "expectedRequired"
	| "patternRequired"
	| "patternTooLong"
	| "patternInvalid"
	| "schemaRequired"
	| "schemaInvalidJson"
	| "schemaKeyword"
	| "mathExpected"
	| "toleranceInvalid"
	| "constraintEmpty"
	| "constraintWords"
	| "constraintContains"
	| "constraintFormat";

const isObject = (value: unknown): value is BenchmarkVerifierConfig =>
	typeof value === "object" && value !== null && !Array.isArray(value);

const nonEmptyString = (value: unknown): boolean => typeof value === "string" && value.trim().length > 0;

/**
 * A number as the node's `mathAnswer` reads one: a plain decimal, a numeric string, or a fraction such as `1/2`.
 * Thousands separators and currency noise are stripped exactly as `BenchmarkMathAnswer.TryParseNumber` strips them.
 */
export function parseMathAnswerNumber(value: unknown): number | null {
	if (typeof value === "number") {
		return Number.isFinite(value) ? value : null;
	}
	if (typeof value !== "string") {
		return null;
	}
	const cleaned = value.replace(/[,_ $€£¥%]/g, "").replace(/[.;:)\]}*"']+$/, "");
	const slash = cleaned.indexOf("/");
	if (slash > 0) {
		const numerator = Number(cleaned.slice(0, slash));
		const denominator = Number(cleaned.slice(slash + 1));
		const quotient = numerator / denominator;
		return denominator !== 0 && Number.isFinite(quotient) ? quotient : null;
	}
	return cleaned.length > 0 && Number.isFinite(Number(cleaned)) ? Number(cleaned) : null;
}

function schemaIssue(schema: unknown): BenchmarkVerifierIssue | null {
	if (!isObject(schema)) {
		return "schemaRequired";
	}
	for (const [keyword, value] of Object.entries(schema)) {
		if (!supportedSchemaKeywords.includes(keyword)) {
			return "schemaKeyword";
		}
		if (keyword === "properties" && isObject(value)) {
			for (const property of Object.values(value)) {
				const nested = schemaIssue(property);
				if (nested !== null) {
					return nested;
				}
			}
		}
		if (keyword === "items") {
			const nested = schemaIssue(value);
			if (nested !== null) {
				return nested;
			}
		}
	}
	return null;
}

const toleranceIssue = (config: BenchmarkVerifierConfig, key: string): boolean => {
	const value = config[key];
	return value !== undefined && (typeof value !== "number" || !Number.isFinite(value) || value < 0);
};

function constraintIssue(config: BenchmarkVerifierConfig): BenchmarkVerifierIssue | null {
	const { minWords, maxWords, mustContain, mustNotContain, format } = config as {
		minWords?: unknown;
		maxWords?: unknown;
		mustContain?: unknown;
		mustNotContain?: unknown;
		format?: unknown;
	};
	if (minWords === undefined && maxWords === undefined && mustContain === undefined && mustNotContain === undefined && format === undefined) {
		return "constraintEmpty";
	}
	const words = [minWords, maxWords].filter((value) => value !== undefined);
	if (words.some((value) => typeof value !== "number" || !Number.isInteger(value) || value < 0)) {
		return "constraintWords";
	}
	if (typeof minWords === "number" && typeof maxWords === "number" && minWords > maxWords) {
		return "constraintWords";
	}
	for (const list of [mustContain, mustNotContain]) {
		if (list !== undefined && (!Array.isArray(list) || list.some((entry) => !nonEmptyString(entry)))) {
			return "constraintContains";
		}
	}
	if (format !== undefined && !benchmarkConstraintFormats.includes(format as BenchmarkConstraintFormat)) {
		return "constraintFormat";
	}
	return null;
}

/**
 * The node's rules, re-checked here. Two of them it cannot fully mirror, and both fail SAFE — this returns null and the
 * node refuses:
 *
 * ponytail: a JavaScript `RegExp` accepts lookaround, backreferences and atomic groups, which the node's
 * `RegexOptions.NonBacktracking` refuses outright. So this catches a syntax error and the node catches the rest; the
 * upgrade path is a linear-time-construct check here, which is a parser, for a message the save already produces.
 */
export function verifierConfigIssue(kind: BenchmarkCriterionKind, config: string | null | undefined): BenchmarkVerifierIssue | null {
	if (kind === "llm") {
		return config === null || config === undefined ? null : "llmConfig";
	}
	const parsed = parseVerifierConfig(config);
	switch (kind) {
		case "exact":
			return nonEmptyString(parsed["expected"]) ? null : "expectedRequired";
		case "regex": {
			const pattern = parsed["pattern"];
			if (!nonEmptyString(pattern)) {
				return "patternRequired";
			}
			if ((pattern as string).length > maxVerifierPatternLength) {
				return "patternTooLong";
			}
			try {
				new RegExp(pattern as string);
				return null;
			} catch {
				return "patternInvalid";
			}
		}
		case "jsonSchema":
			return config !== null && config !== undefined && Object.keys(parsed).length === 0
				? "schemaInvalidJson"
				: schemaIssue(parsed["schema"]);
		case "mathAnswer": {
			if (parseMathAnswerNumber(parsed["expected"]) === null) {
				return "mathExpected";
			}
			return toleranceIssue(parsed, "relativeTolerance") || toleranceIssue(parsed, "absoluteTolerance")
				? "toleranceInvalid"
				: null;
		}
		default:
			return constraintIssue(parsed);
	}
}

/** The first criterion the node would refuse, so the editor can point at one row rather than reporting "invalid". */
export function firstVerifierIssue(
	criteria: readonly { kind?: string | null; config?: string | null }[],
): { index: number; issue: BenchmarkVerifierIssue } | null {
	for (const [index, criterion] of criteria.entries()) {
		const issue = verifierConfigIssue(toBenchmarkCriterionKind(criterion.kind), criterion.config);
		if (issue !== null) {
			return { index, issue };
		}
	}
	return null;
}
