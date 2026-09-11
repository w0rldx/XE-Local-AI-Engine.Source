// The closed vocabularies and their narrowing. These are the half that fails SILENTLY: an un-narrowed member renders
// its own translation key at an operator, and a busy status the badge does not know about disables nothing.

import { describe, expect, it } from "vitest";

import {
	containerRuntimeStatuses,
	EXTERNAL_APP_SECRET_SENTINEL,
	externalAppEventKinds,
	externalAppFailureCategories,
	externalAppPermissionNames,
	externalAppStatuses,
	isExternalAppBusy,
	isExternalAppCancellable,
	toContainerRuntimeStatus,
	toExternalAppEventKind,
	toExternalAppFailureCategory,
	toExternalAppStatus,
} from "@/features/externalApps/models/ExternalAppModels";

describe("external app vocabularies", () => {
	it("narrows every known token to itself", () => {
		expect(externalAppStatuses.map(toExternalAppStatus)).toEqual([...externalAppStatuses]);
		expect(externalAppFailureCategories.map(toExternalAppFailureCategory)).toEqual([...externalAppFailureCategories]);
		expect(containerRuntimeStatuses.map(toContainerRuntimeStatus)).toEqual([...containerRuntimeStatuses]);
		expect(externalAppEventKinds.map(toExternalAppEventKind)).toEqual([...externalAppEventKinds]);
	});

	it("falls back rather than rendering a token a newer server invented", () => {
		expect(toExternalAppStatus("Hibernating")).toBe("Stopped");
		expect(toExternalAppFailureCategory("SolarFlare")).toBe("Unknown");
		expect(toContainerRuntimeStatus("Rebooting")).toBe("ProbeFailed");
		// Not one of the sixteen: the history row renders `events.kind.unknown`, never the raw identifier.
		expect(toExternalAppEventKind("quantumLeapt")).toBe("Unknown");
	});

	it("falls back for a missing value as well as an unknown one", () => {
		expect(toExternalAppStatus(null)).toBe("Stopped");
		expect(toExternalAppStatus(undefined)).toBe("Stopped");
		expect(toExternalAppEventKind(undefined)).toBe("Unknown");
		expect(toContainerRuntimeStatus(null)).toBe("ProbeFailed");
		expect(toExternalAppFailureCategory(undefined)).toBe("Unknown");
	});

	it("counts exactly the members the node's enums declare", () => {
		expect(externalAppStatuses).toHaveLength(10);
		expect(externalAppFailureCategories).toHaveLength(13);
		expect(containerRuntimeStatuses).toHaveLength(7);
		expect(externalAppEventKinds).toHaveLength(16);
	});

	// The `addedPermissions` vocabulary is CLOSED: a name outside it highlights nothing and renders nothing, so the
	// eight members are the whole contract with the server's per-service diff.
	it("keeps the eight addedPermissions names", () => {
		expect([...externalAppPermissionNames]).toEqual([
			"internet",
			"localNetwork",
			"hostFiles",
			"gpu",
			"capabilities",
			"writableRootFilesystem",
			"publishedPorts",
			"extraHosts",
		]);
	});
});

describe("isExternalAppBusy", () => {
	it("is true for exactly the six in-flight statuses", () => {
		expect(externalAppStatuses.filter(isExternalAppBusy)).toEqual([
			"Installing",
			"Starting",
			"Stopping",
			"Updating",
			"Resetting",
			"Uninstalling",
		]);
	});

	// `StoppedUnexpectedly` is settled: it is doing nothing and needs a human, so a spinner there would be a lie.
	it("treats StoppedUnexpectedly and Failed as settled", () => {
		expect(isExternalAppBusy("StoppedUnexpectedly")).toBe(false);
		expect(isExternalAppBusy("Failed")).toBe(false);
	});
});

describe("isExternalAppCancellable", () => {
	it("is true for exactly the three long operations", () => {
		expect(externalAppStatuses.filter(isExternalAppCancellable)).toEqual(["Installing", "Updating", "Resetting"]);
	});
});

describe("EXTERNAL_APP_SECRET_SENTINEL", () => {
	// Written as a LITERAL on purpose. It must equal `ExternalAppVariableMask.Value` on the node, the two sides cannot
	// share a symbol, and a mismatch silently overwrites a stored secret with the mask instead of keeping it.
	it("is the literal the node masks stored secrets with", () => {
		expect(EXTERNAL_APP_SECRET_SENTINEL).toBe("__XE_EXTERNAL_APP_SECRET_UNCHANGED__");
	});
});
