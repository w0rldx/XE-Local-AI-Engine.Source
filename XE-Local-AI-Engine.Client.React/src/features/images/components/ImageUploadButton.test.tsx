// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ImageUploadButton } from "@/features/images/components/ImageUploadButton";
import en from "@/locales/en.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const toastError = vi.fn();
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: (...args: unknown[]) => toastError(...args) } }));

setupMswServer();

// jsdom does not suppress a change event for a re-picked identical file the way a browser does, so a second pick
// proves nothing on its own. What lets a browser re-pick is FileButton's reset, which assigns "" to the input value.
function spyOnInputReset(container: HTMLElement) {
	const input = container.querySelector('input[type="file"]') as HTMLInputElement;
	return vi.spyOn(input, "value", "set");
}

function pick(container: HTMLElement, file: File): void {
	const input = container.querySelector('input[type="file"]') as HTMLInputElement;
	fireEvent.change(input, { target: { files: [file] } });
}

describe("ImageUploadButton", () => {
	afterEach(() => {
		cleanup();
		toastError.mockClear();
	});

	it("offers PNG and JPEG only, under the shipped label", () => {
		const { container } = renderWithProviders(<ImageUploadButton />);

		expect(screen.getByTestId("image-upload-button").textContent).toBe(en.pages.images.uploads.upload);
		expect(container.querySelector('input[type="file"]')?.getAttribute("accept")).toBe("image/png,image/jpeg");
	});

	it("posts the chosen file as multipart", async () => {
		const posted: string[] = [];
		server.use(
			http.post(localApiPath("images/uploads"), ({ request }) => {
				posted.push(request.headers.get("content-type") ?? "");
				return HttpResponse.json({
					imageId: "77777777-7777-4777-8777-777777777777",
					mimeType: "image/png",
					width: 640,
					height: 480,
					createdAtUtc: 0,
				});
			}),
			http.get(localApiPath("images/uploads"), () => HttpResponse.json({ items: [], totalCount: 0 })),
		);
		const { container } = renderWithProviders(<ImageUploadButton />);

		pick(container, new File(["png"], "cat.png", { type: "image/png" }));

		await waitFor(() => {
			expect(posted).toHaveLength(1);
		});
		expect(posted[0]).toContain("multipart/form-data");
		expect(toastError).not.toHaveBeenCalled();
	});

	it("shows the node's refusal sentence", async () => {
		server.use(
			http.post(localApiPath("images/uploads"), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Bad Request", status: 400, detail: "The image is not a PNG or JPEG." },
					{ status: 400, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		const { container } = renderWithProviders(<ImageUploadButton />);

		pick(container, new File(["gif"], "anim.png", { type: "image/png" }));

		await waitFor(() => {
			expect(toastError).toHaveBeenCalledWith("The image is not a PNG or JPEG.");
		});
	});

	it("lets the same file be picked again after a refusal", async () => {
		let posts = 0;
		server.use(
			http.post(localApiPath("images/uploads"), () => {
				posts += 1;
				return HttpResponse.json(
					{ type: "about:blank", title: "Bad Request", status: 400, detail: "The image is larger than the upload size limit." },
					{ status: 400, headers: { "content-type": "application/problem+json" } },
				);
			}),
		);
		const { container } = renderWithProviders(<ImageUploadButton />);
		const reset = spyOnInputReset(container);
		const file = new File(["png"], "big.png", { type: "image/png" });

		pick(container, file);
		await waitFor(() => {
			expect(toastError).toHaveBeenCalledTimes(1);
			expect(reset).toHaveBeenCalledWith("");
		});
		pick(container, file);

		await waitFor(() => {
			expect(posts).toBe(2);
		});
	});

	it("clears the picker after a successful upload too", async () => {
		let posts = 0;
		server.use(
			http.post(localApiPath("images/uploads"), () => {
				posts += 1;
				return HttpResponse.json({
					imageId: "77777777-7777-4777-8777-777777777777",
					mimeType: "image/png",
					width: 640,
					height: 480,
					createdAtUtc: 0,
				});
			}),
			http.get(localApiPath("images/uploads"), () => HttpResponse.json({ items: [], totalCount: 0 })),
		);
		const { container } = renderWithProviders(<ImageUploadButton />);
		const reset = spyOnInputReset(container);

		pick(container, new File(["png"], "cat.png", { type: "image/png" }));

		await waitFor(() => {
			expect(posts).toBe(1);
			expect(reset).toHaveBeenCalledWith("");
		});
		expect(toastError).not.toHaveBeenCalled();
	});
});
