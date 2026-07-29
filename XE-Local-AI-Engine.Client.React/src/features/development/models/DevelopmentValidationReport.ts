/**
 * The deterministic-validation report body.
 *
 * The report is persisted as an ENCRYPTED artifact blob, not as columns, so the artifact-content endpoint hands it to
 * the client as an opaque `content` string. There is therefore no generated type for it: this hand-written contract
 * mirrors the server record (`JsonSerializerDefaults.Web`, so camelCase on the wire) and is the single place the shape
 * is asserted. Nothing here may be edited into `src/core/api/generated/**` — that tree is regenerated from OpenAPI and
 * the report body never appears in the spec.
 */
export interface DevelopmentTestOutcome {
	readonly adapter: string;
	readonly parsed: boolean;
	readonly discovered: number;
	readonly executed: number;
	readonly passed: number;
	readonly failed: number;
	readonly parseFailureCode: string | null;
	readonly parseFailureDetail: string | null;
}

export interface DevelopmentValidationCommand {
	readonly commandId: string;
	readonly exitCode: number;
	readonly completed: boolean;
	readonly outputTruncated: boolean;
	readonly durationMilliseconds: number;
	readonly standardOutput: string;
	readonly standardError: string;
	/** Non-null only on the command that runs tests; every other command carries no outcome at all. */
	readonly testOutcome: DevelopmentTestOutcome | null;
}

export interface DevelopmentValidationReportBody {
	readonly passed: boolean;
	readonly baseCommit: string;
	readonly subjectHash: string;
	readonly manifestHash: string;
	readonly expectedResultHash: string;
	readonly commandProfileVersion: string;
	readonly commandProfileId: string;
	readonly commandProfileDigest: string;
	readonly failureCode: string | null;
	readonly failureDetail: string | null;
	readonly commands: readonly DevelopmentValidationCommand[];
	readonly completedAtUtc: number;
}

/**
 * The "registered repository with no tests" policy case, spelled as both a report-level `failureCode` and a
 * per-outcome `parseFailureCode`. It is a distinct, actionable state — a validation run that proves nothing about
 * behaviour — and must never be rendered as an ordinary failure or, worse, as a pass.
 */
const noTestsCodes: ReadonlySet<string> = new Set(["no_tests_executed", "no_test_projects"]);

export function isDevelopmentNoTestsCode(code: string | null | undefined): boolean {
	return code !== null && code !== undefined && noTestsCodes.has(code);
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

/**
 * Reads a stored validation report out of the opaque artifact `content` string.
 *
 * Returns `null` for anything that is not a report — absent content, invalid JSON, or a payload without the
 * `commands` evidence array. The caller must render that as an explicit error rather than an empty panel: a report
 * that cannot be read is not a report that passed.
 */
export function parseDevelopmentValidationReport(
	content: string | null | undefined,
): DevelopmentValidationReportBody | null {
	if (typeof content !== "string" || content.length === 0) {
		return null;
	}

	let parsed: unknown;
	try {
		parsed = JSON.parse(content);
	} catch {
		return null;
	}

	if (!isRecord(parsed) || !Array.isArray(parsed["commands"])) {
		return null;
	}

	return parsed as unknown as DevelopmentValidationReportBody;
}
