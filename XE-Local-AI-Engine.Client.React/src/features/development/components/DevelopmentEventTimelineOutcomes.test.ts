// The Dev Mode timeline's outcome column is a CLOSED vocabulary the backend owns: every `AddEventAsync` call site in
// `DevelopmentStore.*.cs` passes one of the 17 literals below, and the column had no label map at all — an operator
// read `TransitionedByOperator` and `Terminalized` as raw camel-case tokens.
//
// Mirrors `features/devWorkflows/I18nParity.test.ts`'s vocabulary block rather than inventing a second shape.
//
// WHAT THIS DOES AND DOES NOT GUARD. The list below is typed by hand and compared against `en.json` only. Nothing
// here reads `DevelopmentStore.*.cs`, so a NEW outcome literal added on the C# side does not fail anything — it
// reaches the operator as a raw token, and a human has to add it here. What it does catch is the half that fails
// silently in the other direction: a label deleted, renamed or misspelled in `en.json` while the backend still
// writes the token. en ↔ de parity for these keys is covered by the repository-wide `src/I18n.test.ts`.
//
// A real backend guard is possible and has a precedent — `scripts/CheckSignalrProxySync.mjs` under
// `pnpm run test:tooling` already scans C# sources from the frontend. Not written: 17 literals that have not
// changed in the feature's lifetime did not earn a scanner.

import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";

type LocaleShape = Record<string, unknown>;

function resolvePath(obj: LocaleShape, path: string): unknown {
	return path.split(".").reduce<unknown>((acc, segment) => {
		if (acc === undefined || acc === null || typeof acc !== "object") {
			return undefined;
		}
		return (acc as LocaleShape)[segment];
	}, obj);
}

// Grepped from every `AddEventAsync(...)` outcome argument in XE-Local-AI-Engine.Client.Persistence/Implementation:
// ProjectsAndTasks (Created, Started, Terminalized, Transitioned, TransitionedByOperator), ArtifactsAndRecovery
// (Attached, Detected, Applied, Cleared, Interrupted, Started, Blocked, Completed) and Validation (Invalidated,
// Passed, Failed, Approved, ChangesRequested, Started).
const developmentEventOutcomes = [
	"Created",
	"Started",
	"Terminalized",
	"Interrupted",
	"Transitioned",
	"TransitionedByOperator",
	"Attached",
	"Detected",
	"Applied",
	"Cleared",
	"Invalidated",
	"Passed",
	"Failed",
	"Approved",
	"ChangesRequested",
	"Blocked",
	"Completed",
] as const;

// The `eventType` argument of the same call sites. All 16 are static literals, unlike the four outcomes that are
// ternaries, so this list is the whole set.
const developmentEventTypes = [
	"ProjectCreated",
	"TaskCreated",
	"TaskTransitioned",
	"AttemptStarted",
	"AttemptTerminalized",
	"AttemptInterrupted",
	"ValidationStarted",
	"ValidationFinalized",
	"ReviewFinalized",
	"EvidenceInvalidated",
	"ArtifactAttached",
	"ApplyStarted",
	"ApplyCompleted",
	"ApplyBlocked",
	"WorkflowPolicyApplied",
	"WorkspaceSecretsDetected",
] as const;

describe.each([
	{ section: "outcome", members: developmentEventOutcomes as readonly string[] },
	{ section: "eventType", members: developmentEventTypes as readonly string[] },
])("development timeline $section labels", ({ section, members }) => {
	it.each(members)("%s has an English label", (member) => {
		expect(resolvePath(en as LocaleShape, `pages.development.timeline.${section}.${member}`), member).toBeTypeOf("string");
	});

	it("labels nothing outside the list above", () => {
		const labelled = Object.keys((resolvePath(en as LocaleShape, `pages.development.timeline.${section}`) ?? {}) as LocaleShape);
		expect(labelled.filter((key) => !members.includes(key))).toHaveLength(0);
	});
});
