/**
 * The body of a Tool node's `<nodeKey>-validation.json` artifact.
 *
 * The report is stored as an encrypted blob, so the artifact-content endpoint hands it over as an opaque `content`
 * string and it never appears in the OpenAPI document — there is no generated type for it. This hand-written contract
 * mirrors the server record (`JsonSerializerDefaults.Web`, so camelCase on the wire) and is the single place its shape
 * is asserted. Nothing here may be edited into `src/core/api/generated/**`.
 *
 * Deliberately NOT Dev Mode's `DevelopmentValidationReportBody`: a Tool node validates a clean checkout of the base
 * commit, so it carries no subject, manifest or expected-result hash and does carry the node key and attempt it ran
 * for. The per-command evidence is the same record on both sides, which is the point — the gate a workflow node
 * applies is the gate Dev Mode applies.
 */
export interface DevWorkflowTestOutcome {
	readonly adapter: string;
	readonly parsed: boolean;
	readonly discovered: number;
	readonly executed: number;
	readonly passed: number;
	readonly failed: number;
	readonly parseFailureCode: string | null;
	readonly parseFailureDetail: string | null;
}

export interface DevWorkflowValidationCommand {
	readonly commandId: string;
	readonly exitCode: number;
	readonly completed: boolean;
	readonly outputTruncated: boolean;
	readonly durationMilliseconds: number;
	/**
	 * Sanitized server-side, and replaced wholesale by a sentence saying so when the whole report would not fit under
	 * `MaxArtifactBytes`. Rendered verbatim either way: the server's own sentence is the honest account of an elision,
	 * and a client that tried to detect it by matching the text would go quiet the day that text is reworded.
	 */
	readonly standardOutput: string;
	readonly standardError: string;
	/** Non-null only on the command that runs tests; every other command carries no outcome at all. */
	readonly testOutcome: DevWorkflowTestOutcome | null;
}

export interface DevWorkflowValidationReportBody {
	readonly passed: boolean;
	readonly nodeKey: string;
	readonly attempt: number;
	readonly baseCommit: string;
	readonly commandProfileId: string;
	readonly commandProfileDigest: string;
	readonly failureCode: string | null;
	readonly failureDetail: string | null;
	readonly commands: readonly DevWorkflowValidationCommand[];
	readonly completedAtUtc: number;
}

/**
 * The code the verdict uses when a declared command left no evidence. On a Tool node it is what a node run that ran
 * out of time reports, because the commands it never reached are exactly the missing evidence.
 */
export const devWorkflowMissingEvidenceCode = "missing_command_evidence";

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

/**
 * Reads a validation report out of the opaque artifact body.
 *
 * Returns `null` for anything that is not one — absent content, invalid JSON, or a payload with no `commands`
 * evidence array. The caller renders that as the raw document rather than as an empty panel: a report that cannot be
 * read is not a report that passed.
 */
export function parseDevWorkflowValidationReport(content: string | null | undefined): DevWorkflowValidationReportBody | null {
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

	return parsed as unknown as DevWorkflowValidationReportBody;
}
