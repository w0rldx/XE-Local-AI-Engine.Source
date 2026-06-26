import type { XeLocalAiEngineClientEndpointsLocalChatV1ConversationUploadedFileResponse as ConversationUploadedFileDto } from "@/core/api/generated";

// Extraction lifecycle of an uploaded attachment. The upload endpoint runs extraction synchronously, so a
// freshly returned file is already resolved (extracted/unsupported/failed) — "pending" exists for completeness
// and any transient client-optimistic row. "unknown" guards an unrecognized server string (never trusted blindly).
export type ChatAttachmentStatus = "pending" | "extracted" | "unsupported" | "failed" | "unknown";

// A conversation attachment as the composer renders it. Mapped from the generated upload DTO so the UI never
// depends on the wire shape's optional/nullable fields directly.
export interface ChatAttachment {
	readonly fileId: string;
	readonly originalFileName: string;
	readonly mimeType: string;
	readonly extension: string;
	readonly sizeBytes: number;
	readonly status: ChatAttachmentStatus;
	readonly extractedChars: number | null;
}

function toAttachmentStatus(raw: string | undefined): ChatAttachmentStatus {
	switch ((raw ?? "").toLowerCase()) {
		case "pending":
			return "pending";
		case "extracted":
			return "extracted";
		case "unsupported":
			return "unsupported";
		case "failed":
			return "failed";
		default:
			return "unknown";
	}
}

// Maps a generated upload DTO to the composer's attachment model, defaulting the wire's optional fields.
export function toChatAttachment(dto: ConversationUploadedFileDto): ChatAttachment {
	return {
		fileId: dto.fileId ?? "",
		originalFileName: dto.originalFileName ?? "",
		mimeType: dto.mimeType ?? "",
		extension: dto.extension ?? "",
		sizeBytes: dto.sizeBytes ?? 0,
		status: toAttachmentStatus(dto.extractionStatus),
		extractedChars: dto.extractedChars ?? null,
	};
}

// A locally-tracked in-flight upload, rendered as an optimistic "uploading" chip until the server responds. The
// percent (0..100) is updated from the axios upload-progress callback so a large file animates rather than hanging.
export interface PendingAttachmentUpload {
	readonly tempId: string;
	readonly name: string;
	readonly sizeBytes: number;
	readonly percent: number;
}

// Formats a byte count for the attachment chip: bytes / KB / MB with one decimal above the KB threshold.
export function formatAttachmentSize(bytes: number): string {
	if (bytes < 1024) {
		return `${bytes} B`;
	}
	if (bytes < 1024 * 1024) {
		return `${(bytes / 1024).toFixed(1)} KB`;
	}
	return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
