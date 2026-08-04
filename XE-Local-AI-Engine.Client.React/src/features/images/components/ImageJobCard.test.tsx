// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageJobCard } from "@/features/images/components/ImageJobCard";
import type { ImageJobProgressView, ImageJobView } from "@/features/images/models/ImageModels";

// The card reads the live timeline through this hook; driving it directly keeps the test on the rendering contract
// rather than on the hub transport (which useImageJobHub.test covers).
let currentProgress: ImageJobProgressView | null = null;
vi.mock("@/features/images/hooks/useImageJobHub", () => ({
	useImageJobProgress: () => currentProgress,
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
		...overrides,
	};
}

function renderCard(view = job()) {
	return render(
		<MantineProvider>
			<ImageJobCard job={view} isCancelling={false} onCancel={() => undefined} />
		</MantineProvider>,
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
		currentProgress = progress({ generationPhase: "Sampling", step: 12, totalSteps: 20, secondsPerIteration: 2, estimatedRemainingMs: 16_000 });

		renderCard();

		expect(screen.getByTestId("image-job-steps").textContent).toContain("Step 12 of 20");
		expect(screen.getByTestId("image-job-eta").textContent).toContain("16");
	});

	// The complaint this feature fixes: a step-only countdown hits zero at the last step and then sits there for the
	// whole VAE decode. The decode must announce itself and show NO countdown at all.
	it("shows a finishing message and no countdown once decoding starts", () => {
		currentProgress = progress({ generationPhase: "Decoding", step: 20, totalSteps: 20, estimatedRemainingMs: 0 });

		renderCard();

		expect(screen.getByTestId("image-job-phase").textContent).toContain("decoding");
		expect(screen.queryByTestId("image-job-eta")).toBeNull();
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
