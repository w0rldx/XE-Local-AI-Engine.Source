// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ChatAttachmentChips } from "@/features/chat/components/ChatAttachmentChips";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";

// Mantine's color-scheme provider reads window.matchMedia on mount; jsdom does not implement it.
Object.defineProperty(window, "matchMedia", {
	writable: true,
	value: vi.fn().mockImplementation((query: string) => ({
		matches: false,
		media: query,
		onchange: null,
		addEventListener: vi.fn(),
		removeEventListener: vi.fn(),
		dispatchEvent: vi.fn(),
	})),
});

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function attachment(overrides: Partial<ChatAttachment> = {}): ChatAttachment {
	return {
		fileId: "file-1",
		originalFileName: "notes.pdf",
		mimeType: "application/pdf",
		extension: ".pdf",
		sizeBytes: 2048,
		status: "extracted",
		extractedChars: 1200,
		...overrides,
	};
}

describe("ChatAttachmentChips", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders nothing when there are no attachments or pending uploads", () => {
		renderWithProviders(<ChatAttachmentChips attachments={[]} pendingUploads={[]} onRemove={vi.fn()} />);

		expect(screen.queryByTestId("chat-attachment-chips")).toBeNull();
	});

	it("renders one chip per attachment with its name and extraction status", () => {
		renderWithProviders(
			<ChatAttachmentChips
				attachments={[
					attachment({ fileId: "file-1", originalFileName: "notes.pdf", status: "extracted" }),
					attachment({ fileId: "file-2", originalFileName: "broken.docx", status: "failed" }),
				]}
				pendingUploads={[]}
				onRemove={vi.fn()}
			/>,
		);

		expect(screen.getAllByTestId("chat-attachment-chip")).toHaveLength(2);
		expect(screen.getByText("notes.pdf")).toBeTruthy();
		expect(screen.getByText("broken.docx")).toBeTruthy();
		const statuses = screen.getAllByTestId("chat-attachment-status").map((node) => node.textContent);
		expect(statuses).toEqual(["Extracted", "Failed"]);
	});

	it("renders an optimistic uploading chip with its percent", () => {
		const pending: PendingAttachmentUpload = { tempId: "temp-1", name: "uploading.txt", sizeBytes: 10, percent: 42 };
		renderWithProviders(<ChatAttachmentChips attachments={[]} pendingUploads={[pending]} onRemove={vi.fn()} />);

		expect(screen.getByTestId("chat-attachment-pending")).toBeTruthy();
		expect(screen.getByText("uploading.txt")).toBeTruthy();
		expect(screen.getByText("42%")).toBeTruthy();
	});

	it("fires onRemove with the file id when the remove button is clicked", () => {
		const onRemove = vi.fn();
		renderWithProviders(
			<ChatAttachmentChips attachments={[attachment({ fileId: "file-7" })]} pendingUploads={[]} onRemove={onRemove} />,
		);

		fireEvent.click(screen.getByTestId("chat-attachment-remove"));

		expect(onRemove).toHaveBeenCalledWith("file-7");
	});

	it("disables the remove button when disabled", () => {
		renderWithProviders(
			<ChatAttachmentChips attachments={[attachment()]} pendingUploads={[]} onRemove={vi.fn()} disabled={true} />,
		);

		expect(screen.getByTestId("chat-attachment-remove").hasAttribute("disabled")).toBe(true);
	});
});
