/**
 * The body of an apply Tool node's `<nodeKey>-apply.json` artifact.
 *
 * Like the validation report, this is an encrypted blob handed over as an opaque `content` string, so it has no
 * generated type and this hand-written contract is the single place its shape is asserted. It mirrors
 * `DevWorkflowApplyCommands.DevWorkflowApplyReport` / `AppliedTask` field for field, under
 * `JsonSerializerDefaults.Web` — camelCase on the wire. Nothing here may be edited into `src/core/api/generated/**`.
 *
 * Deliberately NOT the validation report's shape, and the server says why at its end too: that document describes
 * commands run against a workspace, and filling its command list with task applies would be a report claiming evidence
 * it does not have. Both are written under the ordinary `Report` artifact kind, which is why the reader below
 * discriminates on the BODY rather than on a kind or a name.
 */
export interface DevWorkflowAppliedTask {
	/** The node run whose work produced this patch, not the apply node's own key. */
	readonly nodeKey: string;
	readonly taskId: string;
	/** Null when the task carried no title of its own; the caller falls back to the node key. */
	readonly title: string | null;
	/** `applied` | `already-applied` | `blocked` | `refused` | `cancelled`, rendered through the label map below. */
	readonly outcome: string;
	readonly detail: string | null;
}

export interface DevWorkflowApplyReportBody {
	readonly passed: boolean;
	readonly nodeKey: string;
	readonly attempt: number;
	/** Applied PLUS already-applied: the server counts everything the gate did not refuse. */
	readonly tasksApplied: number;
	readonly tasks: readonly DevWorkflowAppliedTask[];
	readonly completedAtUtc: number;
}

/**
 * Only these two mean the repository has the patch. Everything else is a task whose work did NOT land.
 *
 * The set is closed server-side (`AppliedOutcomes`) and is NOT narrowed here: the panel renders the token through a
 * label map with the raw token as its fallback, so a vocabulary a newer server invents reads as itself rather than
 * disappearing. `pages.devWorkflows.applyOutcome.*` is where the set is pinned, and the i18n parity test is what keeps
 * that map complete.
 */
export function isDevWorkflowApplyLanded(outcome: string): boolean {
	return outcome === "applied" || outcome === "already-applied";
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null;
}

/**
 * Reads an apply report out of the opaque artifact body.
 *
 * Returns `null` for anything that is not one — absent content, invalid JSON, or a payload with no `tasks` list. A
 * validation report reaches this reader too (both are artifact kind `Report`), and answering `null` for it is what
 * keeps the two panels apart: `tasks` and `commands` are the two documents' respective evidence arrays, and neither
 * carries the other's.
 */
export function parseDevWorkflowApplyReport(content: string | null | undefined): DevWorkflowApplyReportBody | null {
	if (typeof content !== "string" || content.length === 0) {
		return null;
	}

	let parsed: unknown;
	try {
		parsed = JSON.parse(content);
	} catch {
		return null;
	}

	if (!isRecord(parsed) || !Array.isArray(parsed["tasks"]) || typeof parsed["tasksApplied"] !== "number") {
		return null;
	}

	return parsed as unknown as DevWorkflowApplyReportBody;
}
