// @vitest-environment jsdom

// The comparison walks every tensor of two files, so a render that changes neither must not repeat it.

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

const { compareSpy } = vi.hoisted(() => ({ compareSpy: vi.fn() }));
vi.mock("@/features/models/models/GgufInspectComparison", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/features/models/models/GgufInspectComparison")>();
	return {
		...actual,
		compareInspections: (...args: Parameters<typeof actual.compareInspections>) => {
			compareSpy();
			return actual.compareInspections(...args);
		},
	};
});
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	getModelGgufInspectionOptions: (options: { path: { modelName: string } }) => ({
		queryKey: ["getModelGgufInspection", options.path.modelName],
		queryFn: () => Promise.resolve(file("Q8_0")),
	}),
}));

import { MantineProvider } from "@mantine/core";
import { QueryClientProvider } from "@tanstack/react-query";

import { ModelInspectComparison } from "@/features/models/components/ModelInspectComparison";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";
import { createTestQueryClient } from "@/test/RenderWithProviders";

function file(ggmlType: string) {
	return {
		modelName,
		readFailure: null,
		ggufVersion: 3,
		architecture: "qwen35",
		displayName: "Tiny",
		parameterCount: 1000,
		fileType: ggmlType,
		blockCount: 1,
		attentionHeadCount: 16,
		attentionHeadCountKv: 4,
		embeddingLength: 4096,
		contextLength: 262144,
		expertCount: null,
		chatTemplate: null,
		splitCount: null,
		tensorCount: 1,
		totalTensorBytes: 100,
		headerBytes: 2048,
		fileSizeBytes: 4096,
		blocks: [
			{
				index: 0,
				byteSize: 100,
				tensors: [{ name: "blk.0.a", shape: [256], ggmlType, ggmlTypeId: 12, offset: 0, byteSize: 100 }],
			},
		],
		nonBlockTensors: [],
	};
}

const modelName = "org/Tiny-GGUF:Q4_K_M";
const otherName = "org/Tiny-GGUF:Q8_0";

describe("ModelInspectComparison", () => {
	afterEach(() => {
		cleanup();
		compareSpy.mockClear();
	});

	it("compares once across an unrelated render, and again only for a new inspection", async () => {
		installJsdomEnvironmentMocks();
		const queryClient = createTestQueryClient();
		const wrapper = ({ children }: { children: ReactNode }) => (
			<QueryClientProvider client={queryClient}>
				<MantineProvider env="test" theme={testMantineTheme}>
					{children}
				</MantineProvider>
			</QueryClientProvider>
		);
		const inspection = file("Q4_K");
		const { rerender } = render(
			<ModelInspectComparison modelName={modelName} inspection={inspection} candidates={[otherName]} />,
			{ wrapper },
		);

		fireEvent.click(screen.getByTestId("model-inspect-compare-select"));
		fireEvent.click(await screen.findByRole("option", { name: "Q8_0", hidden: true }));
		const result = await screen.findByTestId("model-inspect-compare-result");
		expect(compareSpy).toHaveBeenCalledTimes(1);

		// A new candidates array re-renders the comparison with everything it compares unchanged.
		rerender(<ModelInspectComparison modelName={modelName} inspection={inspection} candidates={[otherName]} />);

		expect(screen.getByTestId("model-inspect-compare-result")).toBe(result);
		expect(compareSpy).toHaveBeenCalledTimes(1);

		rerender(<ModelInspectComparison modelName={modelName} inspection={file("Q5_K")} candidates={[otherName]} />);

		expect(compareSpy).toHaveBeenCalledTimes(2);
	});
});
