// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock generated query/mutation factories to isolate the hook while retaining validation and mapping.
const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		listLocalModelsOptions: vi.fn(),
		getToolCapableModelsOptions: vi.fn(),
		listFn: vi.fn(),
		toolCapableFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>();
	return {
		...actual,
		listLocalModelsOptions: generatedMock.listLocalModelsOptions,
		getToolCapableModelsOptions: generatedMock.getToolCapableModelsOptions,
	};
});

import { useTeacherModelNames } from "@/features/training/queries/useTrainingDatasets";

function wrapper({ children }: { children: ReactNode }) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

function arrange(modelNames: string[], toolCapable: string[]): void {
	generatedMock.listFn.mockResolvedValue({ items: modelNames.map((modelName) => ({ modelName })) });
	generatedMock.toolCapableFn.mockResolvedValue({ models: toolCapable });
	generatedMock.listLocalModelsOptions.mockReturnValue({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: generatedMock.listFn,
	});
	generatedMock.getToolCapableModelsOptions.mockReturnValue({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getToolCapableModels" }],
		queryFn: generatedMock.toolCapableFn,
	});
}

async function teacherNames(modelNames: string[], toolCapable: string[]): Promise<string[]> {
	arrange(modelNames, toolCapable);
	const { result } = renderHook(() => useTeacherModelNames(), { wrapper });
	await waitFor(() => expect(result.current.length).toBeGreaterThan(0));
	return result.current;
}

describe("useTeacherModelNames — external-provider exclusion", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("drops external models from the teacher/critic list even when they are tool-capable", async () => {
		const names = await teacherNames(["qwen3:8b", "ext:unsloth-box/qwen3-27b"], ["qwen3:8b", "ext:unsloth-box/qwen3-27b"]);

		expect(names).toEqual(["qwen3:8b"]);
	});

	it("drops external models on the un-narrowed path too, where the node reports no tool-capable set", async () => {
		const names = await teacherNames(["qwen3:8b", "ext:gateway/gpt-4o"], []);

		expect(names).toEqual(["qwen3:8b"]);
	});

	it("does not fall back to offering external models when they are the only tool-capable ones", async () => {
		// Without filtering the allow-list too, narrowing would produce an empty set and the "do not enforce"
		// fallback would then hand back every local name — external ones included.
		const names = await teacherNames(["qwen3:8b", "ext:gateway/gpt-4o"], ["ext:gateway/gpt-4o"]);

		expect(names).toEqual(["qwen3:8b"]);
	});

	it("leaves an all-local list untouched", async () => {
		const names = await teacherNames(["qwen3:8b", "llama3:70b"], ["qwen3:8b"]);

		expect(names).toEqual(["qwen3:8b"]);
	});
});
