import type {
	XeLocalAiEngineClientEndpointsKnowledgeV1ImportKnowledgeRepositoryResponse,
	XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeDocumentDetailResponse,
	XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeDocumentResponse,
	XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeSearchHitResponse,
	XeLocalAiEngineClientServicesKnowledgeKnowledgeDocumentStatus,
} from "@/core/api/generated";

// Local aliases for the (verbose) generated DTO names so the feature code reads cleanly. The generated types stay
// the single source of truth — these are pure re-exports, not parallel shapes.
export type KnowledgeDocumentStatus = XeLocalAiEngineClientServicesKnowledgeKnowledgeDocumentStatus;
export type KnowledgeDocument = XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeDocumentResponse;
export type KnowledgeDocumentDetail = XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeDocumentDetailResponse;
export type KnowledgeSearchHit = XeLocalAiEngineClientEndpointsKnowledgeV1KnowledgeSearchHitResponse;
export type KnowledgeRepositoryImportResult = XeLocalAiEngineClientEndpointsKnowledgeV1ImportKnowledgeRepositoryResponse;

export const KNOWLEDGE_DEFAULT_COLLECTION_ID = "DEFAULT";

/** Mirrors the server's bounded, path-safe collection-id contract for immediate UI feedback. */
export function normalizeKnowledgeCollectionId(value: string): string | undefined {
	const normalized = value.trim().toUpperCase();
	return normalized.length > 0 && normalized.length <= 128 && /^[A-Z0-9._-]+$/.test(normalized) ? normalized : undefined;
}

// Advisory upload guards. The endpoint re-enforces both (extension allow-list + size cap + extraction) — these
// only avoid a guaranteed-reject round trip and give the operator instant feedback. Mirrors the chat-attachment
// surface's advisory-then-server-authoritative posture.
export const KNOWLEDGE_MAX_UPLOAD_SIZE_MB = 25;
export const KNOWLEDGE_MAX_UPLOAD_SIZE_BYTES = KNOWLEDGE_MAX_UPLOAD_SIZE_MB * 1024 * 1024;

// Deterministic plaintext/code formats handled by the backend PlaintextDocumentReader. Keep this explicit list in
// parity with that reader: the browser guard is advisory, but hiding a server-supported extension makes a valid local
// document impossible to select through the normal file picker.
export const KNOWLEDGE_DETERMINISTIC_TEXT_EXTENSIONS: readonly string[] = [
	".txt",
	".text",
	".md",
	".markdown",
	".csv",
	".tsv",
	".json",
	".jsonc",
	".log",
	".cs",
	".ts",
	".tsx",
	".js",
	".jsx",
	".mjs",
	".cjs",
	".py",
	".java",
	".go",
	".rs",
	".cpp",
	".cc",
	".cxx",
	".c",
	".h",
	".hpp",
	".hh",
	".html",
	".htm",
	".xml",
	".xaml",
	".yaml",
	".yml",
	".toml",
	".ini",
	".cfg",
	".conf",
	".properties",
	".env",
	".sh",
	".bash",
	".zsh",
	".ps1",
	".bat",
	".sql",
	".css",
	".scss",
	".sass",
	".less",
	".rb",
	".php",
	".kt",
	".kts",
	".swift",
	".scala",
	".pl",
	".lua",
	".r",
	".vb",
	".fs",
	".fsx",
	".gradle",
	".dockerfile",
	".gitignore",
	".editorconfig",
];

// Structured document readers registered by DocumentTextExtractor in addition to deterministic plaintext/code.
export const KNOWLEDGE_STRUCTURED_DOCUMENT_EXTENSIONS: readonly string[] = [".pdf", ".docx"];

// Complete backend-supported upload set. Advisory only; the endpoint remains authoritative.
export const KNOWLEDGE_ACCEPTED_EXTENSIONS: readonly string[] = [
	...KNOWLEDGE_DETERMINISTIC_TEXT_EXTENSIONS,
	...KNOWLEDGE_STRUCTURED_DOCUMENT_EXTENSIONS,
];

// The `accept` attribute value for the hidden file input (comma-separated extension list).
export const KNOWLEDGE_ACCEPT_ATTRIBUTE = KNOWLEDGE_ACCEPTED_EXTENSIONS.join(",");

export interface KnowledgeStatusDescriptor {
	readonly status: KnowledgeDocumentStatus;
	// Mantine semantic color for the status pill.
	readonly color: string;
	// i18n key for the human-readable status label.
	readonly labelKey: string;
	// In-progress states (queued + the extract→chunk→embed pipeline) render an animated spinner in the pill.
	readonly inProgress: boolean;
}

// Status → visual descriptor. Terminal states are sharp accents (Indexed green, Failed red); the whole
// ingestion pipeline (Pending/Extracting/Chunking/Embedding) reads as one "working" amber state with a spinner.
const STATUS_DESCRIPTORS: Record<KnowledgeDocumentStatus, KnowledgeStatusDescriptor> = {
	Pending: { status: "Pending", color: "yellow", labelKey: "pages.knowledgeBase.status.pending", inProgress: true },
	Extracting: { status: "Extracting", color: "yellow", labelKey: "pages.knowledgeBase.status.extracting", inProgress: true },
	Chunking: { status: "Chunking", color: "yellow", labelKey: "pages.knowledgeBase.status.chunking", inProgress: true },
	Embedding: { status: "Embedding", color: "yellow", labelKey: "pages.knowledgeBase.status.embedding", inProgress: true },
	Indexed: { status: "Indexed", color: "green", labelKey: "pages.knowledgeBase.status.indexed", inProgress: false },
	Failed: { status: "Failed", color: "red", labelKey: "pages.knowledgeBase.status.failed", inProgress: false },
};

export function knowledgeStatusDescriptor(status: KnowledgeDocumentStatus): KnowledgeStatusDescriptor {
	return STATUS_DESCRIPTORS[status];
}

const BYTE_UNITS: readonly string[] = ["B", "KB", "MB", "GB", "TB"];

// Human-readable byte size (e.g. "1.4 MB"). Whole bytes for the B unit, one decimal above it.
export function formatKnowledgeBytes(bytes: number): string {
	if (!Number.isFinite(bytes) || bytes <= 0) {
		return "0 B";
	}
	const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), BYTE_UNITS.length - 1);
	const value = bytes / 1024 ** exponent;
	return `${value.toFixed(exponent === 0 ? 0 : 1)} ${BYTE_UNITS[exponent]}`;
}

// createdAtUtc / updatedAtUtc ride the wire as epoch MILLISECONDS (matches the agent-execution-log convention).
export function formatKnowledgeTimestamp(epochMs: number): string {
	if (!Number.isFinite(epochMs) || epochMs <= 0) {
		return "—";
	}
	const date = new Date(epochMs);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

// Returns the lower-cased extension of a filename (including the leading dot), or "" when there is none.
function fileExtension(fileName: string): string {
	const lastDot = fileName.lastIndexOf(".");
	return lastDot >= 0 ? fileName.slice(lastDot).toLowerCase() : "";
}

export function isAcceptedKnowledgeFile(fileName: string): boolean {
	return KNOWLEDGE_ACCEPTED_EXTENSIONS.includes(fileExtension(fileName));
}
