// @vitest-environment jsdom

// Lineage of an edited job: how it edited and what it started from. The source may be gone by the time the job is
// viewed — the node nulls the job's sourceImageId, or a still-cached id answers 404 — and both must read as a plain
// "source removed" line, never as an error.

import { cleanup, screen } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageEditLineage } from "@/features/images/components/ImageSourceThumbnail";
import type { ImageJobView } from "@/features/images/models/ImageModels";
import en from "@/locales/en.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const toastError = vi.fn();
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: (...args: unknown[]) => toastError(...args) } }));

setupMswServer();

const sourceId = "55555555-5555-4555-8555-555555555555";

function job(overrides: Partial<ImageJobView> = {}): ImageJobView {
	return {
		id: "job-1",
		modelName: "sd-1.5",
		prompt: "make it autumn",
		negativePrompt: null,
		status: "Succeeded",
		seed: 42,
		width: 512,
		height: 512,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		createdAtUtc: 0,
		startedAtUtc: 0,
		completedAtUtc: 0,
		durationMs: 1000,
		imageId: "66666666-6666-4666-8666-666666666666",
		sanitizedError: null,
		editMode: "img2img",
		sourceImageId: sourceId,
		strength: 0.5,
		...overrides,
	};
}

describe("ImageEditLineage", () => {
	beforeEach(() => {
		// jsdom has no object URLs; the hook only needs a string back for the <img>.
		Object.assign(URL, { createObjectURL: () => "blob:source", revokeObjectURL: () => undefined });
	});
	afterEach(() => {
		cleanup();
		toastError.mockClear();
	});

	it("shows the edit mode with its strength and the source thumbnail", async () => {
		server.use(
			http.get(
				localApiPath(`images/${sourceId}`),
				() => new HttpResponse(new Blob(["png"]), { headers: { "content-type": "image/png" } }),
			),
		);

		renderWithProviders(<ImageEditLineage job={job()} />);

		expect(screen.getByTestId("image-edit-detail").textContent).toBe(`${en.pages.images.edit.modes.img2img} · strength 0.5`);
		expect(screen.getByText(en.pages.images.edit.editedFrom)).toBeTruthy();
		expect((await screen.findByTestId("image-source-thumbnail")).getAttribute("src")).toBe("blob:source");
	});

	it("labels a source the node no longer has as removed, without an error toast", async () => {
		server.use(
			http.get(localApiPath(`images/${sourceId}`), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Not Found", status: 404, detail: "" },
					{ status: 404, headers: { "content-type": "application/problem+json" } },
				),
			),
		);

		renderWithProviders(<ImageEditLineage job={job()} />);

		expect((await screen.findByTestId("image-source-removed")).textContent).toBe(en.pages.images.edit.sourceRemoved);
		expect(screen.queryByTestId("image-source-thumbnail")).toBeNull();
		expect(toastError).not.toHaveBeenCalled();
	});

	it("labels a nulled source as removed without asking the node", () => {
		renderWithProviders(<ImageEditLineage job={job({ editMode: "reference", sourceImageId: null, strength: null })} />);

		expect(screen.getByTestId("image-edit-detail").textContent).toBe(en.pages.images.edit.modes.reference);
		expect(screen.getByTestId("image-source-removed").textContent).toBe(en.pages.images.edit.sourceRemoved);
	});

	it("renders nothing for a text-to-image job", () => {
		renderWithProviders(<ImageEditLineage job={job({ editMode: null, sourceImageId: null, strength: null })} />);

		expect(screen.queryByTestId("image-edit-lineage")).toBeNull();
	});
});
