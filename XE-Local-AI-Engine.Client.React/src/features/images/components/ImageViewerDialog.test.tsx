// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageViewerDialog } from "@/features/images/components/ImageViewerDialog";
import type { ImageJobView } from "@/features/images/models/ImageModels";

const objectUrlResult: { url: string | undefined; blob: Blob | undefined; isLoading: boolean; isError: boolean } = {
	url: "blob:generated-image",
	blob: undefined,
	isLoading: false,
	isError: false,
};

vi.mock("@/features/images/hooks/useImageObjectUrl", () => ({
	useImageObjectUrl: () => objectUrlResult,
}));

// No i18next instance is initialised under vitest, so the real useTranslation returns the default string with the
// {{placeholders}} intact. This interpolating stub (same shape as McpServerToolsPanel's) lets the metadata assertions
// check the actual values rather than the template.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

// Only the DOM-touching half is stubbed; buildGeneratedImageFileName stays real so this test also pins that the
// download action passes the sanitized name (its own edge cases live in GeneratedImageDownload.test.ts).
const downloadSpy = vi.fn();
vi.mock("@/features/images/GeneratedImageDownload", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/features/images/GeneratedImageDownload")>();
	return {
		...actual,
		downloadGeneratedImage: (blob: Blob, fileName: string) => downloadSpy(blob, fileName),
	};
});

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function job(overrides: Partial<ImageJobView> = {}): ImageJobView {
	return {
		id: "11111111-1111-1111-1111-111111111111",
		modelName: "sd-1.5",
		prompt: "a red fox in snow",
		negativePrompt: null,
		status: "Succeeded",
		seed: 182_736,
		width: 1024,
		height: 1024,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		createdAtUtc: 0,
		startedAtUtc: 0,
		completedAtUtc: 0,
		durationMs: 12_000,
		imageId: "22222222-2222-2222-2222-222222222222",
		sanitizedError: null,
		...overrides,
	};
}

describe("ImageViewerDialog", () => {
	beforeEach(() => {
		downloadSpy.mockReset();
		objectUrlResult.url = "blob:generated-image";
		objectUrlResult.blob = new Blob(["png-bytes"], { type: "image/png" });
		objectUrlResult.isLoading = false;
		objectUrlResult.isError = false;
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addListener: vi.fn(),
				removeListener: vi.fn(),
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		// DialogShell's body is a Mantine ScrollArea.Autosize, which observes its own size; jsdom ships no
		// ResizeObserver, so the dialog throws on mount without this stub (same shim as DialogShell's own test).
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the full-size image and its generation metadata", async () => {
		renderWithProviders(<ImageViewerDialog job={job()} opened={true} onClose={vi.fn()} />);

		const image = await screen.findByTestId("image-viewer-image");
		expect(image.getAttribute("src")).toBe("blob:generated-image");
		expect(screen.getByTestId("image-viewer-prompt").textContent).toBe("a red fox in snow");
		expect(screen.getByTestId("image-viewer-seed").textContent).toBe("182736");
		expect(screen.queryByTestId("image-viewer-seed-random")).toBeNull();
		expect(screen.getByTestId("image-viewer-settings").textContent).toContain("1024×1024");
		expect(screen.getByTestId("image-viewer-settings").textContent).toContain("euler_a");
		expect(screen.getByTestId("image-viewer-duration").textContent).toBe("12s");
	});

	it("omits the negative prompt row when the job had none", async () => {
		renderWithProviders(<ImageViewerDialog job={job()} opened={true} onClose={vi.fn()} />);

		await screen.findByTestId("image-viewer-image");
		expect(screen.queryByTestId("image-viewer-negative-prompt")).toBeNull();
	});

	it("shows the negative prompt when the job had one", async () => {
		renderWithProviders(
			<ImageViewerDialog job={job({ negativePrompt: "blurry, low quality" })} opened={true} onClose={vi.fn()} />,
		);

		const negative = await screen.findByTestId("image-viewer-negative-prompt");
		expect(negative.textContent).toBe("blurry, low quality");
	});

	it("downloads the in-memory blob under a sanitized file name", async () => {
		renderWithProviders(<ImageViewerDialog job={job()} opened={true} onClose={vi.fn()} />);

		fireEvent.click(await screen.findByTestId("image-viewer-download"));

		expect(downloadSpy).toHaveBeenCalledTimes(1);
		expect(downloadSpy).toHaveBeenCalledWith(objectUrlResult.blob, "xe-image-sd-1.5-seed-182736.png");
	});

	// The bytes are what get saved, so the action must stay disabled until they have actually arrived — otherwise the
	// button looks live during the fetch and silently does nothing.
	it("disables the download until the bytes have arrived", async () => {
		objectUrlResult.blob = undefined;
		renderWithProviders(<ImageViewerDialog job={job()} opened={true} onClose={vi.fn()} />);

		const button = await screen.findByTestId("image-viewer-download");
		expect((button as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(button);
		expect(downloadSpy).not.toHaveBeenCalled();
	});

	it("surfaces a load failure instead of a broken image", async () => {
		objectUrlResult.isError = true;
		objectUrlResult.url = undefined;
		objectUrlResult.blob = undefined;

		renderWithProviders(<ImageViewerDialog job={job()} opened={true} onClose={vi.fn()} />);

		await waitFor(() => expect(screen.getByTestId("image-viewer-error")).toBeTruthy());
		expect(screen.queryByTestId("image-viewer-image")).toBeNull();
	});

	// The pinned sd-server never reports the seed it drew, so a random-seed job keeps the -1 sentinel. Offering that
	// as a copyable "seed" would hand the operator a value that reproduces nothing.
	it("labels a random seed instead of showing the -1 sentinel", async () => {
		renderWithProviders(<ImageViewerDialog job={job({ seed: -1 })} opened={true} onClose={vi.fn()} />);

		await screen.findByTestId("image-viewer-image");
		expect(screen.getByTestId("image-viewer-seed-random").textContent).toContain("Random");
		expect(screen.queryByTestId("image-viewer-seed")).toBeNull();
		expect(screen.queryByTestId("image-viewer-copy-seed")).toBeNull();
	});

	it("downloads a random-seed image under a 'random' file name", async () => {
		renderWithProviders(<ImageViewerDialog job={job({ seed: -1 })} opened={true} onClose={vi.fn()} />);

		fireEvent.click(await screen.findByTestId("image-viewer-download"));

		expect(downloadSpy).toHaveBeenCalledWith(objectUrlResult.blob, "xe-image-sd-1.5-random.png");
	});

	it("renders no dialog content while closed", () => {
		renderWithProviders(<ImageViewerDialog job={job()} opened={false} onClose={vi.fn()} />);

		expect(screen.queryByTestId("image-viewer-image")).toBeNull();
	});
});
