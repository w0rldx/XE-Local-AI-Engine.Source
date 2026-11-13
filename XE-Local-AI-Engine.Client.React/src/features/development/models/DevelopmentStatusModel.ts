const nextActionStatuses = new Set(["Planned", "Ready", "InProgress", "ChangesRequested", "InReview"]);

/**
 * Whether the task has a next action to offer. A task a missing test runner stood down has one: once the runner is
 * installed, the same patch is validated again (the server refuses while a workflow run still drives the task).
 */
export function canStartNextAction(status: string | undefined, blockedReason: string | null | undefined): boolean {
	return (
		nextActionStatuses.has(status ?? "") || (status === "Blocked" && blockedReason?.startsWith("[runner_not_installed]") === true)
	);
}

export function operationId(): string {
	return globalThis.crypto.randomUUID();
}

export function statusColor(status?: string): string {
	if (status === "Completed" || status === "Succeeded" || status === "AwaitingApply") {
		return "green";
	}
	if (status === "Failed" || status === "Blocked" || status === "Cancelled") {
		return "red";
	}
	if (status === "Interrupted" || status === "ChangesRequested") {
		return "yellow";
	}
	return "blue";
}

/**
 * The next-action button's label as a translation key paired with its English default.
 *
 * The label names the SPECIFIC action the engine will take next, which is the only thing on the page that tells the
 * operator what the button is about to do. Returning the key rather than the sentence keeps that selection here, in
 * plain control flow, while leaving the wording to the locale files.
 */
export function nextActionLabel(status: string | undefined, latestAttemptStatus: string | undefined): readonly [string, string] {
	if (latestAttemptStatus === "Interrupted") {
		return ["pages.development.nextAction.replacement", "Start replacement attempt"];
	}
	if (status === "InReview") {
		return ["pages.development.nextAction.review", "Start independent review"];
	}
	if (status === "InProgress" && latestAttemptStatus === "Succeeded") {
		return ["pages.development.nextAction.validation", "Run deterministic validation"];
	}
	if (status === "ChangesRequested") {
		return ["pages.development.nextAction.revision", "Start coder revision"];
	}
	if (status === "Blocked") {
		return ["pages.development.nextAction.retryValidation", "Retry validation"];
	}
	return ["pages.development.nextAction.default", "Start next action"];
}
