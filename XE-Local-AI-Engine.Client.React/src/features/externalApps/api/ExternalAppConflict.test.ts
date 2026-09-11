// The six 409 discriminators this module can receive, read off the thrown `ApiError` rather than off a message string.
// A conflictType that stops being recognised here does not throw — it degrades to a generic toast and the UI keeps
// offering an action the server has already refused, so the member names are asserted literally.

import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";
import {
	externalAppConflictMessageKey,
	externalAppConflictTypes,
	readExternalAppConflict,
} from "@/features/externalApps/api/ExternalAppConflict";

function conflict(status: number, extras: Record<string, unknown>): ApiError {
	return new ApiError(status, {
		type: "about:blank",
		title: "Conflict",
		status,
		detail: "The request conflicts with the current state.",
		...extras,
	} as ProblemDetails);
}

describe("readExternalAppConflict", () => {
	it("reads the conflict type off a 409 envelope", () => {
		const error = conflict(409, { conflictType: "ExternalAppOperationInFlight" });

		expect(readExternalAppConflict(error)).toEqual({
			conflictType: externalAppConflictTypes.operationInFlight,
			addedPermissions: undefined,
		});
	});

	it("carries the widened permissions when an update was posted without the disclosure", () => {
		// The one arm with a typed extra: the SPA never sees the target manifest's effective permissions, so this list
		// is the only thing it can show. Losing it would reopen the dialog with an empty diff.
		const error = conflict(409, {
			conflictType: "ExternalAppPermissionChangeRequiresAcknowledgement",
			addedPermissions: ["gpu", "publishedPorts"],
		});

		expect(readExternalAppConflict(error)).toEqual({
			conflictType: externalAppConflictTypes.permissionChangeRequiresAcknowledgement,
			addedPermissions: ["gpu", "publishedPorts"],
		});
	});

	it("reads nothing off anything that is not a 409 conflict envelope", () => {
		expect(readExternalAppConflict(conflict(400, { conflictType: "ExternalAppVersionConflict" }))).toBeUndefined();
		expect(readExternalAppConflict(conflict(409, {}))).toBeUndefined();
		expect(readExternalAppConflict(new Error("network down"))).toBeUndefined();
		expect(readExternalAppConflict(undefined)).toBeUndefined();
	});
});

describe("externalAppConflictMessageKey", () => {
	it("maps every one of the six conflict types to its own sentence", () => {
		const keys = Object.entries(externalAppConflictTypes).map(([name, conflictType]) => [
			name,
			externalAppConflictMessageKey(conflictType),
		]);

		expect(keys).toEqual([
			["operationInFlight", "pages.externalApps.conflict.operationInFlight"],
			["invalidTransition", "pages.externalApps.conflict.invalidTransition"],
			["alreadyInstalled", "pages.externalApps.conflict.alreadyInstalled"],
			["permissionChangeRequiresAcknowledgement", "pages.externalApps.conflict.permissionChangeRequiresAcknowledgement"],
			["versionConflict", "pages.externalApps.conflict.versionConflict"],
			["manifestChanged", "pages.externalApps.conflict.manifestChanged"],
		]);
	});

	it("has no sentence for a type this build does not know, so the caller falls back to the server's detail", () => {
		expect(externalAppConflictMessageKey("ExternalAppSomethingNewer")).toBeUndefined();
	});
});
