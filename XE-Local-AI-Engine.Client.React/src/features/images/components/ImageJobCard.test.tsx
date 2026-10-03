// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageJobCard } from "@/features/images/components/ImageJobCard";
import type { ImageEditSource, ImageJobProgressView, ImageJobView } from "@/features/images/models/ImageModels";
import en from "@/locales/en.json";
import { testMantineTheme } from "@/test/MantineTestRender";

// The card reads the live timeline through this hook; driving it directly keeps the test on the rendering contract
// rather than on the hub transport (which useImageJobHub.test covers).
let currentProgress: ImageJobProgressView | null = null;
vi.mock("@/features/images/hooks/useImageJobHub", () => ({
	useImageJobProgress: () => currentProgress,
}));

// The result thumbnail and the lineage thumbnail fetch through this hook; the card only needs a URL back.
vi.mock("@/features/images/hooks/useImageObjectUrl", () => ({
	useImageObjectUrl: (imageId: string | null) => ({
		url: imageId ? `blob:${imageId}` : undefined,
		blob: undefined,
		isLoading: false,
		isError: false,
	}),
	imageBlobQueryKey: (imageId: string) => ["image-blob", imageId],
}));

// No i18next instance under vitest, so the real useTranslation would return the template with {{placeholders}}
// intact. This interpolating stub (same shape as ImageViewerDialog.test) lets the assertions read real numbers.
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

function job(overrides: Partial<ImageJobView> = {}): ImageJobView {
	return {
		id: "job-1",
		modelName: "sd-1.5",
		prompt: "a watercolor fox",
		negativePrompt: null,
		status: "Generating",
		seed: 42,
		width: 512,
		height: 512,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		createdAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: null,
		durationMs: null,
		imageId: null,
		sanitizedError: null,
		editMode: null,
		sourceImageId: null,
		strength: null,
		...overrides,
	};
}

function progress(overrides: Partial<ImageJobProgressView>): ImageJobProgressView {
	return {
		seq: 1,
		status: "Generating",
		queuePosition: null,
		generationPhase: null,
		step: null,
		totalSteps: null,
		secondsPerIteration: null,
		estimatedRemainingMs: null,
		decodeStartedAtUtc: null,
		...overrides,
	};
}

// The card now carries a delete button whose mutation needs a client; the timeline assertions below are unaffected.
function renderCard(view = job(), onEdit?: (source: ImageEditSource) => void) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ImageJobCard job={view} isCancelling={false} onCancel={() => undefined} onEdit={onEdit} />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("ImageJobCard generation timeline", () => {
	beforeEach(() => {
		// jsdom does not implement matchMedia; MantineProvider reads it to resolve the colour scheme on mount.
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
	});

	afterEach(() => {
		currentProgress = null;
		cleanup();
	});

	it("shows the step count and the remaining time while sampling", () => {
		currentProgress = progress({
			generationPhase: "Sampling",
			step: 12,
			totalSteps: 20,
			secondsPerIteration: 2,
			estimatedRemainingMs: 16_000,
		});

		renderCard();

		expect(screen.getByTestId("image-job-steps").textContent).toContain("Step 12 of 20");
		expect(screen.getByTestId("image-job-eta").textContent).toContain("16");
	});

	// The complaint this feature fixes: a step-only countdown hits zero at the last step and then sits there for the
	// whole VAE decode. The decode must announce itself and show NO countdown at all.
	it("shows a finishing message and no countdown once decoding starts", () => {
		currentProgress = progress({ generationPhase: "Decoding", step: 20, totalSteps: 20, estimatedRemainingMs: 0 });

		renderCard();

		expect(screen.getByTestId("image-job-phase").textContent).toContain("Decoding image");
		expect(screen.queryByTestId("image-job-eta")).toBeNull();
	});

	// sd-server reports no decode progress, so pushes stop for the whole decode: the line counts UP from the decode
	// start on its own clock, and a later push carrying the same start does not reset it. Never a countdown.
	it("counts the decode up in seconds from its start, across a later push", () => {
		const template = en.pages.images.job.finishing;
		vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
		try {
			vi.setSystemTime(1_700_000_100_000);
			currentProgress = progress({ seq: 7, generationPhase: "Decoding", decodeStartedAtUtc: 1_700_000_088_000 });
			const view = renderCard();
			const phase = (): string | null => screen.getByTestId("image-job-phase").textContent;

			expect(phase()).toBe(template.replace("{{seconds}}", "12"));

			act(() => {
				vi.advanceTimersByTime(3_000);
			});
			expect(phase()).toBe(template.replace("{{seconds}}", "15"));

			currentProgress = progress({ seq: 8, generationPhase: "Decoding", decodeStartedAtUtc: 1_700_000_088_000 });
			act(() => {
				vi.advanceTimersByTime(2_000);
			});
			view.rerender(
				<QueryClientProvider client={new QueryClient()}>
					<MantineProvider env="test" theme={testMantineTheme}>
						<ImageJobCard job={job()} isCancelling={false} onCancel={() => undefined} />
					</MantineProvider>
				</QueryClientProvider>,
			);
			expect(phase()).toBe(template.replace("{{seconds}}", "17"));
			expect(phase()).not.toMatch(/left/i);
			expect(screen.queryByTestId("image-job-eta")).toBeNull();
		} finally {
			vi.useRealTimers();
		}
	});

	it("shows a preparing message and no countdown while the model loads", () => {
		currentProgress = progress({ generationPhase: "Loading" });

		renderCard();

		expect(screen.getByTestId("image-job-phase").textContent).toContain("Preparing");
		expect(screen.queryByTestId("image-job-eta")).toBeNull();
		expect(screen.queryByTestId("image-job-step-progress")).toBeNull();
	});

	it("renders no timeline for a job that has already ended", () => {
		currentProgress = progress({ generationPhase: "Sampling", step: 20, totalSteps: 20, estimatedRemainingMs: 1_000 });

		renderCard(job({ status: "Cancelled" }));

		expect(screen.queryByTestId("image-job-steps")).toBeNull();
		expect(screen.queryByTestId("image-job-eta")).toBeNull();
	});
});

describe("ImageJobCard edit", () => {
	afterEach(cleanup);

	it("hands a succeeded job's image and size to the edit form", () => {
		const onEdit = vi.fn();
		renderCard(job({ status: "Succeeded", imageId: "img-1", width: 768, height: 512 }), onEdit);

		fireEvent.click(screen.getByTestId("image-job-edit"));

		expect(onEdit).toHaveBeenCalledWith({ imageId: "img-1", width: 768, height: 512 });
	});

	it("offers no Edit for a job without an image, or when no model can edit", () => {
		renderCard(job({ status: "Failed", imageId: null }), vi.fn());
		expect(screen.queryByTestId("image-job-edit")).toBeNull();
		cleanup();

		renderCard(job({ status: "Succeeded", imageId: "img-1" }));
		expect(screen.queryByTestId("image-job-edit")).toBeNull();
	});

	it("shows how an edited job was made and what it was edited from", () => {
		renderCard(job({ status: "Succeeded", imageId: "img-2", editMode: "img2img", sourceImageId: "src-1", strength: 0.6 }));

		expect(screen.getByTestId("image-edit-detail").textContent).toBe("Variation · strength 0.6");
		expect(screen.getByText("Edited from")).toBeTruthy();
		expect(screen.getByTestId("image-source-thumbnail").getAttribute("src")).toBe("blob:src-1");
	});
});
