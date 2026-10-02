// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { delay, HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { UploadedImageList } from "@/features/images/components/UploadedImageList";
import en from "@/locales/en.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// Thumbnails are useImageObjectUrl's concern (and need object URLs jsdom lacks); this list is about which state shows.
vi.mock("@/features/images/hooks/useImageObjectUrl", () => ({
	useImageObjectUrl: () => ({ url: "blob:upload", blob: undefined, isLoading: false, isError: false }),
	imageBlobQueryKey: (imageId: string) => ["image-blob", imageId],
}));

setupMswServer();

describe("UploadedImageList", () => {
	afterEach(() => {
		cleanup();
	});

	it("reports a failed list instead of inviting an upload", async () => {
		server.use(http.get(localApiPath("images/uploads"), () => HttpResponse.json({}, { status: 500 })));

		renderWithProviders(<UploadedImageList />);

		const alert = await screen.findByTestId("uploaded-images-error");
		expect(alert.textContent).toContain(en.pages.images.uploads.listError);
		expect(screen.queryByTestId("uploaded-images-empty")).toBeNull();
	});

	it("invites an upload once an empty list has loaded", async () => {
		server.use(http.get(localApiPath("images/uploads"), () => HttpResponse.json({ items: [], totalCount: 0 })));

		renderWithProviders(<UploadedImageList />);

		expect((await screen.findByTestId("uploaded-images-empty")).textContent).toBe(en.pages.images.uploads.empty);
		expect(screen.queryByTestId("uploaded-images-error")).toBeNull();
	});

	it("shows a loader, not the invitation, while the list is loading", async () => {
		server.use(
			http.get(localApiPath("images/uploads"), async () => {
				await delay("infinite");
				return HttpResponse.json({ items: [], totalCount: 0 });
			}),
		);

		renderWithProviders(<UploadedImageList />);

		expect(await screen.findByTestId("uploaded-images-loading")).toBeTruthy();
		expect(screen.queryByTestId("uploaded-images-empty")).toBeNull();
	});

	it("lists each upload as a card", async () => {
		server.use(
			http.get(localApiPath("images/uploads"), () =>
				HttpResponse.json({
					items: [
						{ imageId: "88888888-8888-4888-8888-888888888888", mimeType: "image/png", width: 64, height: 48, createdAtUtc: 0 },
					],
					totalCount: 1,
				}),
			),
		);

		renderWithProviders(<UploadedImageList />);

		expect(await screen.findAllByTestId("uploaded-image-card")).toHaveLength(1);
	});

	it("pages by the node's total and asks for the next page's offset", async () => {
		const offsets: (string | null)[] = [];
		server.use(
			http.get(localApiPath("images/uploads"), ({ request }) => {
				const query = new URL(request.url).searchParams;
				offsets.push(query.get("offset"));
				const imageId =
					query.get("offset") === "0" ? "88888888-8888-4888-8888-888888888888" : "99999999-9999-4999-8999-999999999999";
				return HttpResponse.json({
					items: [{ imageId, mimeType: "image/png", width: 64, height: 48, createdAtUtc: 0 }],
					totalCount: 12,
				});
			}),
		);

		renderWithProviders(<UploadedImageList />);

		const pagination = await screen.findByTestId("uploaded-images-pagination");
		expect(screen.getByTestId("uploaded-images-pagination-range").textContent).toContain("1–10 of 12");
		fireEvent.click(within(pagination).getByText("2"));

		await waitFor(() => {
			expect(screen.getByTestId("uploaded-images-pagination-range").textContent).toContain("11–12 of 12");
		});
		expect(offsets).toEqual(["0", "10"]);
	});
});
