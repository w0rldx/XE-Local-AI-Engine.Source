// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { UploadedImageCard } from "@/features/images/components/UploadedImageCard";
import type { UploadedImageView } from "@/features/images/models/ImageModels";
import en from "@/locales/en.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The thumbnail fetch is useImageObjectUrl's own concern (and needs object URLs jsdom lacks); this card is about
// what it shows and which actions it wires.
vi.mock("@/features/images/hooks/useImageObjectUrl", () => ({
	useImageObjectUrl: () => ({ url: "blob:upload", blob: undefined, isLoading: false, isError: false }),
	imageBlobQueryKey: (imageId: string) => ["image-blob", imageId],
}));

setupMswServer();

const image: UploadedImageView = {
	imageId: "88888888-8888-4888-8888-888888888888",
	width: 4000,
	height: 3000,
	createdAtUtc: 0,
};

describe("UploadedImageCard", () => {
	afterEach(cleanup);

	it("shows the upload label and its size, with no prompt", () => {
		renderWithProviders(<UploadedImageCard image={image} onEdit={vi.fn()} />);

		expect(screen.getByText(en.pages.images.uploads.uploaded)).toBeTruthy();
		expect(screen.getByTestId("uploaded-image-size").textContent).toBe("4000×3000");
		expect(screen.queryByTestId("image-job-prompt")).toBeNull();
	});

	it("starts an edit from the upload with its own dimensions", () => {
		const onEdit = vi.fn();
		renderWithProviders(<UploadedImageCard image={image} onEdit={onEdit} />);

		fireEvent.click(screen.getByTestId("uploaded-image-edit"));

		expect(screen.getByTestId("uploaded-image-edit").textContent).toBe(en.pages.images.edit.action);
		expect(onEdit).toHaveBeenCalledWith({ imageId: image.imageId, width: 4000, height: 3000 });
	});

	it("hides Edit when no installed model can edit", () => {
		renderWithProviders(<UploadedImageCard image={image} />);

		expect(screen.queryByTestId("uploaded-image-edit")).toBeNull();
	});

	it("deletes the upload on the node", async () => {
		let deleted = false;
		server.use(
			http.delete(localApiPath(`images/uploads/${image.imageId}`), () => {
				deleted = true;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderWithProviders(<UploadedImageCard image={image} onEdit={vi.fn()} />);

		fireEvent.click(screen.getByTestId("uploaded-image-delete"));

		await waitFor(() => {
			expect(deleted).toBe(true);
		});
	});
});
