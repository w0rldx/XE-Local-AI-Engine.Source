// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ApprovedImage } from "@/features/model-fit/models/ModelFitModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
	}),
}));

const { hooksMock } = vi.hoisted(() => ({
	hooksMock: {
		useApprovedImages: vi.fn(),
	},
}));

vi.mock("@/features/model-fit/queries/useModelFit", () => hooksMock);

import { ApprovedImagesPage } from "@/features/model-fit/pages/ApprovedImagesPage";

const sampleImage: ApprovedImage = {
	approvedImageId: "llmfit-recommender-0-9-30",
	displayName: "llmfit recommender",
	description: "Approved llmfit recommendation utility",
	purpose: ["ModelRecommendation", "ModelBenchmark"],
	imageReference: "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a519",
	sourceUrl: "https://github.com/AlexsJones/llmfit",
	upstreamVersion: "0.9.30",
	enabled: true,
	deprecatedAtUtc: null,
	replacementApprovedImageId: null,
	lastUsedAtUtc: 1_700_000_000_000,
	lastSuccessfulRunAtUtc: 1_700_000_000_000,
	diagnostics: null,
};

function makeQuery<T>(data: T) {
	return { data, isLoading: false, error: null };
}

function installJsdomEnvironmentMocks(): void {
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
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<ApprovedImagesPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("ApprovedImagesPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useApprovedImages.mockReturnValue(makeQuery([sampleImage]));
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the read-only approved-images table with the pinned reference", () => {
		renderPage();

		expect(screen.getByTestId("model-fit-approved-images-table")).toBeTruthy();
		const row = screen.getByTestId("model-fit-approved-image-row-llmfit-recommender-0-9-30");
		expect(within(row).getByText("llmfit recommender")).toBeTruthy();
		expect(
			screen.getByTestId("model-fit-approved-image-reference-llmfit-recommender-0-9-30").textContent,
		).toBe("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a519");
	});

	it("renders no editing controls (read-only)", () => {
		const { container } = renderPage();

		expect(container.querySelectorAll("input").length).toBe(0);
		expect(container.querySelectorAll("button").length).toBe(0);
	});

	it("shows the empty state when no images are registered", () => {
		hooksMock.useApprovedImages.mockReturnValue(makeQuery([]));

		renderPage();

		expect(screen.getByTestId("model-fit-approved-images-empty")).toBeTruthy();
	});

	it("surfaces a load error", () => {
		hooksMock.useApprovedImages.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPage();

		expect(screen.getByTestId("model-fit-approved-images-error")).toBeTruthy();
	});
});
