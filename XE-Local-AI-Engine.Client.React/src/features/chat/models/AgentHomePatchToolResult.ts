/** The tool whose result can carry an exported patch. Matched exactly; no other tool exports one. */
const AGENT_HOME_TOOL_NAME = "run_in_agent_home";

/**
 * The node-authored header the AgentHome tool result opens with:
 *
 * ```
 * [agent-home run=<runId> outcome=<Token> patch=<exported|none>]\n
 * ```
 *
 * **Anchored at the start of the string, with no multiline flag**, so `^` can only match position 0 and a
 * header-shaped line appearing anywhere later is not a header. That anchoring is the whole security property here,
 * not a tidiness choice: everything after the header is free text the MODEL influences. `run_command` takes an
 * arbitrary `executable` string by design, and the summary echoes it back verbatim — so a scan of the prose for
 * something that *looks* like a run reference lets a model name a run of its choosing and redirect the operator's
 * "review and apply" button at a different run's patch. The header is composed by the node before any of that text,
 * and only position 0 is a place the model cannot write.
 *
 * There is deliberately **no fallback** to searching the body. A result with no header gets no button: a missing
 * header means the node did not say which run this was, and guessing is exactly the thing that went wrong.
 */
const AGENT_HOME_RESULT_HEADER = /^\[agent-home run=([A-Za-z0-9][A-Za-z0-9_-]*) outcome=[A-Za-z]+ patch=(exported|none)\]\n/;

/**
 * The run id whose exported patch this tool result points at, or `null` when the node reported none — the call
 * exported no patch, failed, or carries no header at all. Returning `null` is what keeps the apply affordance off a
 * card that has nothing the operator can act on.
 */
export function agentHomeRunIdWithPatch(toolName: string, result: string | undefined): string | null {
	if (toolName !== AGENT_HOME_TOOL_NAME || result === undefined) {
		return null;
	}

	const header = AGENT_HOME_RESULT_HEADER.exec(result);
	if (header === null || header[2] !== "exported") {
		return null;
	}

	return header[1] ?? null;
}
