// Domain view-models for the Codex OAuth sign-in card. Separate from the raw API DTOs so the component
// never touches wire-format optionality.

export type CodexSignInState =
	| { kind: "signed-out" }
	| { kind: "pending"; authorizeUrl: string }
	| { kind: "signed-in"; accountId: string; expiresAtUtc: string | null }
	| { kind: "expired" }
	| { kind: "error"; message: string };
