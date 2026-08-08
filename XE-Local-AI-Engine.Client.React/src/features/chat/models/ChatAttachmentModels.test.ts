import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsLocalChatV1ConversationUploadedFileResponse as ConversationUploadedFileDto } from "@/core/api/generated";
import { formatAttachmentSize, toChatAttachment } from "@/features/chat/models/ChatAttachmentModels";

// Fills the required wire fields so a test can override only what it exercises. sizeBytes defaults to 0 and
// extractedChars stays omitted (optional) so the coalescing assertions below still observe the mapper defaults.
function fileDto(overrides: Partial<ConversationUploadedFileDto> = {}): ConversationUploadedFileDto {
	return {
		fileId: "file-1",
		conversationId: "conversation-1",
		originalFileName: "notes.pdf",
		mimeType: "application/pdf",
		extension: ".pdf",
		sizeBytes: 0,
		extractionStatus: "Extracted",
		createdAtUtc: 1_700_000_000,
		...overrides,
	};
}

describe("toChatAttachment", () => {
	it("maps a fully-populated upload DTO to the attachment model", () => {
		const attachment = toChatAttachment({
			fileId: "file-1",
			conversationId: "conversation-1",
			originalFileName: "notes.pdf",
			mimeType: "application/pdf",
			extension: ".pdf",
			sizeBytes: 2048,
			extractionStatus: "Extracted",
			extractedChars: 1200,
			createdAtUtc: 1_700_000_000,
		});

		expect(attachment).toEqual({
			fileId: "file-1",
			originalFileName: "notes.pdf",
			mimeType: "application/pdf",
			extension: ".pdf",
			sizeBytes: 2048,
			status: "extracted",
			extractedChars: 1200,
		});
	});

	it("normalizes the extraction status case-insensitively and defaults missing fields", () => {
		expect(toChatAttachment(fileDto({ extractionStatus: "FAILED" })).status).toBe("failed");
		expect(toChatAttachment(fileDto({ extractionStatus: "Pending" })).status).toBe("pending");
		expect(toChatAttachment(fileDto({ extractionStatus: "Unsupported" })).status).toBe("unsupported");
		// An unrecognized server string is never trusted blindly — it maps to "unknown", not the raw value.
		expect(toChatAttachment(fileDto({ extractionStatus: "weird-value" })).status).toBe("unknown");
		expect(toChatAttachment(fileDto()).extractedChars).toBeNull();
		expect(toChatAttachment(fileDto()).sizeBytes).toBe(0);
	});
});

describe("formatAttachmentSize", () => {
	it("formats bytes, KB, and MB with the expected granularity", () => {
		expect(formatAttachmentSize(512)).toBe("512 B");
		expect(formatAttachmentSize(2048)).toBe("2.0 KB");
		expect(formatAttachmentSize(5 * 1024 * 1024)).toBe("5.0 MB");
	});
});
