// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NodeSettingsFieldsCard } from "@/features/node-settings/components/NodeSettingsFieldsCard";
import {
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

// Deterministic i18n: t returns the supplied default (with {{var}} interpolation applied) so the human copy is
// asserted, not the raw key — this doubles as the i18n-keys-resolve check (the card never renders a bare dotted key).
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string, vars?: Record<string, unknown>) => {
			const text = fallback ?? _key;
			if (vars === undefined) {
				return text;
			}
			return Object.entries(vars).reduce(
				(acc, [name, value]) => acc.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
	}),
}));

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
}

interface RenderOverrides {
	onDownloadRecommendedReranker?: () => void;
	isDownloadRecommendedRerankerPending?: boolean;
	isRecommendedRerankerInFlight?: boolean;
}

function renderCard(overrides: RenderOverrides = {}): { onDownload: () => void } {
	const onDownload = overrides.onDownloadRecommendedReranker ?? vi.fn();
	render(
		<MantineProvider>
			<NodeSettingsFieldsCard
				form={toNodeSettingsFieldsForm(undefined)}
				bounds={toNodeSettingsFieldBounds(undefined)}
				errors={{}}
				onChange={vi.fn()}
				showDeveloperFields={false}
				draftModelOptions={[]}
				rerankerModelOptions={[]}
				onDownloadRecommendedReranker={onDownload}
				isDownloadRecommendedRerankerPending={overrides.isDownloadRecommendedRerankerPending ?? false}
				isRecommendedRerankerInFlight={overrides.isRecommendedRerankerInFlight ?? false}
			/>
		</MantineProvider>,
	);
	return { onDownload };
}

describe("NodeSettingsFieldsCard — recommended reranker download", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders the download button and the recommended-model helper line", () => {
		renderCard();

		expect(screen.getByTestId("node-settings-reranker-download-recommended")).toBeTruthy();
		// The helper names the recommended model + its extra model server (human copy resolves — no bare i18n key).
		expect(screen.getByText(/bge-reranker-v2-m3/)).toBeTruthy();
	});

	it("invokes the download handler once when the button is clicked", () => {
		const { onDownload } = renderCard();

		fireEvent.click(screen.getByTestId("node-settings-reranker-download-recommended"));

		expect(onDownload).toHaveBeenCalledTimes(1);
	});

	it("disables the button while the recommended reranker download is in flight (duplicate-guard)", () => {
		renderCard({ isRecommendedRerankerInFlight: true });

		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables the button while the download request is pending", () => {
		renderCard({ isDownloadRecommendedRerankerPending: true });

		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});
});
