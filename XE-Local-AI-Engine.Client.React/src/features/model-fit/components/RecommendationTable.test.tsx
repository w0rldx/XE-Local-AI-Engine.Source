// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Deterministic i18n: t returns the supplied default string so button labels are readable in assertions.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

import { RecommendationTable } from "@/features/model-fit/components/RecommendationTable";
import type { ModelFitRecommendation } from "@/features/model-fit/models/ModelFitModels";

function makeRecommendation(overrides: Partial<ModelFitRecommendation>): ModelFitRecommendation {
	return {
		rank: 1,
		modelName: "model-a",
		providerModelName: null,
		score: 8,
		fitLevel: "Good",
		runMode: "gpu",
		quantization: "Q4_0",
		estimatedTokensPerSecond: 42,
		requiredRamMb: 6144,
		requiredVramMb: 4096,
		contextTokens: 8192,
		isInstalled: false,
		pullModelName: null,
		...overrides,
	};
}

function renderTable(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("RecommendationTable", () => {
	beforeEach(() => {
		// Mantine's MantineProvider reads window.matchMedia / ResizeObserver on mount; jsdom omits both.
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
	});

	afterEach(() => {
		cleanup();
	});

	it("shows a Pull button only for a not-installed row with a pullModelName and calls onPull with that row", () => {
		const onPull = vi.fn();
		const pullable = makeRecommendation({ rank: 1, modelName: "pullable", isInstalled: false, pullModelName: "pullable:latest" });
		const installed = makeRecommendation({ rank: 2, modelName: "installed", isInstalled: true, pullModelName: "installed:latest" });
		const noTag = makeRecommendation({ rank: 3, modelName: "no-tag", isInstalled: false, pullModelName: null });

		renderTable(<RecommendationTable recommendations={[pullable, installed, noTag]} onPull={onPull} />);

		// Only the pullable row (rank 1) exposes a Pull button.
		const pullButton = screen.getByTestId("model-fit-pull-button-1");
		expect(pullButton).toBeTruthy();
		// Installed row and the null-tag row carry no Pull button.
		expect(screen.queryByTestId("model-fit-pull-button-2")).toBeNull();
		expect(screen.queryByTestId("model-fit-pull-button-3")).toBeNull();

		fireEvent.click(pullButton);
		expect(onPull).toHaveBeenCalledTimes(1);
		expect(onPull).toHaveBeenCalledWith(pullable);
	});

	it("disables the in-flight row's Pull button when pullingModelName matches its tag", () => {
		const onPull = vi.fn();
		const pullable = makeRecommendation({ rank: 1, modelName: "pullable", isInstalled: false, pullModelName: "pullable:latest" });

		renderTable(<RecommendationTable recommendations={[pullable]} onPull={onPull} pullingModelName="pullable:latest" />);

		const pullButton = screen.getByTestId("model-fit-pull-button-1") as HTMLButtonElement;
		expect(pullButton.disabled).toBe(true);
	});

	it("renders no action column when onPull is not provided (pure-presentation mode)", () => {
		const pullable = makeRecommendation({ rank: 1, isInstalled: false, pullModelName: "pullable:latest" });

		renderTable(<RecommendationTable recommendations={[pullable]} />);

		expect(screen.queryByTestId("model-fit-pull-button-1")).toBeNull();
	});
});
