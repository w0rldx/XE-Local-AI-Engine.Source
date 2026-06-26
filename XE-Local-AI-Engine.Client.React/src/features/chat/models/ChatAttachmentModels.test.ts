import { describe, expect, it } from "vitest";

import { formatAttachmentSize, toChatAttachment } from "@/features/chat/models/ChatAttachmentModels";

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
		expect(toChatAttachment({ extractionStatus: "FAILED" }).status).toBe("failed");
		expect(toChatAttachment({ extractionStatus: "Pending" }).status).toBe("pending");
		expect(toChatAttachment({ extractionStatus: "Unsupported" }).status).toBe("unsupported");
		// An unrecognized server string is never trusted blindly — it maps to "unknown", not the raw value.
		expect(toChatAttachment({ extractionStatus: "weird-value" }).status).toBe("unknown");
		expect(toChatAttachment({}).extractedChars).toBeNull();
		expect(toChatAttachment({}).sizeBytes).toBe(0);
	});
});

describe("formatAttachmentSize", () => {
	it("formats bytes, KB, and MB with the expected granularity", () => {
		expect(formatAttachmentSize(512)).toBe("512 B");
		expect(formatAttachmentSize(2048)).toBe("2.0 KB");
		expect(formatAttachmentSize(5 * 1024 * 1024)).toBe("5.0 MB");
	});
});
