import { ApiError } from "@/core/api/errors/ApiError";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";

/**
 * The `conflictType` discriminators the node's global ConflictExceptionHandler writes for this module. They mirror
 * `NodeConflictProblemType` members (XE-Local-AI-Engine.Client/Common/ProblemDetailModels/Enums/), which serialize as
 * the enum NAME — rename one there and these strings must follow.
 */
export const externalAppConflictTypes = {
	/** Another operation on this instance is still running. The action stays enabled; it is a "try again" refusal. */
	operationInFlight: "ExternalAppOperationInFlight",
	/** The verb does not fit the instance's current state — the tab is stale, so the caller re-reads. */
	invalidTransition: "ExternalAppInvalidTransition",
	/** This application already has an instance. V1 allows exactly one, so the card flips to Details. */
	alreadyInstalled: "ExternalAppAlreadyInstalled",
	/** An update widens the grant and was posted without the disclosure. Carries `addedPermissions`. */
	permissionChangeRequiresAcknowledgement: "ExternalAppPermissionChangeRequiresAcknowledgement",
	/** The row moved between the read and the write: `expectedVersion` is stale on any lifecycle call or a save. */
	versionConflict: "ExternalAppVersionConflict",
	/** The catalog moved between disclosure and submit, so the accepted fingerprint no longer describes what is on offer. */
	manifestChanged: "ExternalAppManifestChanged",
} as const;

export interface ExternalAppConflict {
	readonly conflictType: string;
	/**
	 * Permission-change refusal only: the names the update widens, from the closed eight-name vocabulary, carried on the
	 * problem details the way `DevWorkflowGateAlreadyDecided` carries `standingDecision`. The SPA cannot compute the
	 * widening — it never sees the target manifest's effective permissions — so it renders this list and nothing else.
	 */
	readonly addedPermissions?: readonly string[];
}

/**
 * Reads the 409 envelope off a thrown error, or `undefined` for anything that is not one.
 *
 * Matches the thrown `ApiError`, not an axios error: the response interceptor rethrows every non-2xx as `ApiError`, so
 * `isAxiosError` is already false by the time a component sees it.
 */
export function readExternalAppConflict(error: unknown): ExternalAppConflict | undefined {
	if (!(error instanceof ApiError) || error.statusCode !== 409) {
		return undefined;
	}
	const problemDetails = error.apiProblemDetails as Partial<ConflictProblemDetails & { addedPermissions: string[] }> | undefined;
	if (!problemDetails?.conflictType) {
		return undefined;
	}
	return { conflictType: problemDetails.conflictType, addedPermissions: problemDetails.addedPermissions };
}

/**
 * The two conflicts whose cause is a STALE ROW rather than a stale catalog: the `expectedVersion` the caller echoed
 * describes a row that has since moved. A retry has to be made against a re-read, so every call site that sees one
 * invalidates the instance AND the list before it lets the operator try again — the list is what Start reads its
 * version from, and leaving it cached hands the next click the same rejected token.
 */
export function isExternalAppStaleRowConflict(conflictType: string | undefined): boolean {
	return conflictType === externalAppConflictTypes.versionConflict || conflictType === externalAppConflictTypes.invalidTransition;
}

const conflictMessageKeys: ReadonlyMap<string, string> = new Map(
	Object.entries(externalAppConflictTypes).map(([key, conflictType]) => [conflictType, `pages.externalApps.conflict.${key}`]),
);

/**
 * Conflict type → its sentence key. An unknown type yields `undefined` and the caller falls back to
 * `apiErrorMessage(error, …)`, which surfaces the server's own `detail` rather than a guess.
 */
export function externalAppConflictMessageKey(conflictType: string): string | undefined {
	return conflictMessageKeys.get(conflictType);
}
