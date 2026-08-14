// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

// The dialog previews with a trimmed path but historically started the import with the raw, untrimmed one; the
// backend compares them ordinally and rejects the mismatch with InvalidPreviewToken. Stub both mutationFns so the
// spies can assert exactly what string each call receives.
const { previewSpy, startSpy } = vi.hoisted(() => ({ previewSpy: vi.fn(), startSpy: vi.fn() }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	previewGgufImportMutation: () => ({ mutationFn: previewSpy }),
	startGgufImportMutation: () => ({ mutationFn: startSpy }),
}));

import type { XeLocalAiEngineClientEndpointsModelFitV1PreviewGgufImportResponse } from "@/core/api/generated";
import { GgufImportDialog } from "@/features/models/components/GgufImportDialog";

const preview: XeLocalAiEngineClientEndpointsModelFitV1PreviewGgufImportResponse = {
	previewToken: "preview-token-1",
	sourceDisplayName: "model.gguf",
	sizeBytes: 1024,
	ggufVersion: 3,
	architecture: "llama",
	modelBaseName: "model",
	detectedQuantization: "Q4_K_M",
	canonicalQuantizationChoices: ["Q4_K_M", "Q5_K_M"],
	canonicalModelName: "model:Q4_K_M",
	hasSufficientStorage: true,
	warnings: [],
	expiresAtUtc: "2026-08-14T00:10:00Z",
};

function renderDialog() {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<GgufImportDialog opened={true} onClose={vi.fn()} onStarted={vi.fn()} />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("GgufImportDialog", () => {
	beforeEach(() => {
		previewSpy.mockResolvedValue(preview);
		startSpy.mockResolvedValue({ operationId: "op-1" });
		// Mantine reads the color scheme from matchMedia; jsdom doesn't implement it.
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
		// DialogShell's ScrollArea (Mantine) uses a ResizeObserver; jsdom doesn't implement it.
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
		vi.clearAllMocks();
	});

	it("starts the import with the same trimmed path used for the preview", async () => {
		renderDialog();

		fireEvent.change(screen.getByLabelText(/GGUF file path/), { target: { value: "  /models/model.gguf  " } });
		fireEvent.click(screen.getByText("Preview import"));

		await waitFor(() => expect(previewSpy).toHaveBeenCalledTimes(1));
		expect(previewSpy.mock.calls[0]?.[0]?.body?.sourcePath).toBe("/models/model.gguf");

		fireEvent.click(screen.getByText("Import model"));

		await waitFor(() => expect(startSpy).toHaveBeenCalledTimes(1));
		expect(startSpy.mock.calls[0]?.[0]?.body?.sourcePath).toBe("/models/model.gguf");
	});
});
