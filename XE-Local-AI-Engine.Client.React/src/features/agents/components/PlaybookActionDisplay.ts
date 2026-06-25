import type { MemoryScope, PlaybookActionSource } from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitorStatus } from "@/features/agents/models/PlaybookMonitorModels";

// Adaptive-memory scopes in display order for the filter + badge. Kept in one place so the SegmentedControl options,
// the badge color map, and the i18n keys all stay in sync.
export const MEMORY_SCOPES: readonly MemoryScope[] = ["Procedural", "Failure", "UserPreference", "Project"];

// Mantine badge color per memory scope. Failure is rendered red (negative guidance — "don't do X"); the others use
// distinct neutral/positive hues so the scope reads at a glance.
export const memoryScopeColors: Record<MemoryScope, string> = {
	Procedural: "blue",
	Failure: "red",
	UserPreference: "violet",
	Project: "cyan",
};

// English fallback copy per scope (the i18n key carries the localized text).
export const memoryScopeFallbacks: Record<MemoryScope, string> = {
	Procedural: "Procedural",
	Failure: "Failure",
	UserPreference: "User preference",
	Project: "Project",
};

// English fallback copy per provenance source. "Extracted" reads as "Extracted from run" so the operator knows the
// candidate was harvested from a completed conversation by the adaptive-memory extractor (not hand-authored).
export const sourceFallbacks: Record<PlaybookActionSource, string> = {
	Manual: "Manual",
	Analysis: "Analysis",
	Extracted: "Extracted from run",
};

// The Mantine badge color per monitor verdict. Improved is positive (teal), Regressed negative
// (red), Flat/InsufficientData neutral (gray) so the signal reads at a glance.
export const monitorStatusColors: Record<PlaybookMonitorStatus, string> = {
	Improved: "teal",
	Regressed: "red",
	Flat: "gray",
	InsufficientData: "gray",
};

// English fallback copy per verdict (the i18n key carries the localized text). "InsufficientData" reads as a
// short human phrase rather than the wire token.
export const monitorStatusFallbacks: Record<PlaybookMonitorStatus, string> = {
	Improved: "Improved",
	Regressed: "Regressed",
	Flat: "Flat",
	InsufficientData: "Insufficient data",
};
