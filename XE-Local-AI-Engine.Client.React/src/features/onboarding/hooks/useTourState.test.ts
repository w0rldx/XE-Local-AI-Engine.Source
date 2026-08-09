// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
	clearTutorialProgress,
	readTutorialProgress,
	tutorialProgressStorageKey,
	useTutorialState,
	writeTutorialProgress,
} from "@/features/onboarding/hooks/useTourState";

const { useQueryMock, useMutationMock, mutateMock } = vi.hoisted(() => ({
	useQueryMock: vi.fn(),
	useMutationMock: vi.fn(),
	mutateMock: vi.fn(),
}));

vi.mock("@tanstack/react-query", () => ({
	useQuery: useQueryMock,
	useMutation: useMutationMock,
	useQueryClient: () => ({ invalidateQueries: vi.fn().mockResolvedValue(undefined) }),
}));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getTutorialStateOptions: () => ({}),
	getTutorialStateQueryKey: () => ["tutorial-state"],
	saveTutorialStateMutation: () => ({}),
}));

function prime(entries: { key: string; status: string }[]) {
	useQueryMock.mockReturnValue({ data: { entries }, isSuccess: true, isError: false });
	useMutationMock.mockReturnValue({ mutate: mutateMock });
}

afterEach(() => {
	localStorage.clear();
	vi.clearAllMocks();
});

describe("tutorial progress", () => {
	it("round-trips the versioned step-id payload per tutorial key", () => {
		writeTutorialProgress("agents-v1", "agentsCreate");
		expect(readTutorialProgress("agents-v1", ["agentsOverview", "agentsCreate"])).toEqual({
			format: 1,
			stepId: "agentsCreate",
		});
	});

	it.each(["3", "not-json", '{"format":2,"stepId":"chatInput"}', '{"format":1,"stepId":"removed"}'])(
		"clears legacy, malformed, unsupported, or unknown progress: %s",
		(raw) => {
			const key = tutorialProgressStorageKey("main-app-v1");
			localStorage.setItem(key, raw);
			expect(readTutorialProgress("main-app-v1", ["chatInput"])).toBeNull();
			expect(localStorage.getItem(key)).toBeNull();
		},
	);

	it("clears only the selected tutorial progress", () => {
		writeTutorialProgress("agents-v1", "agentsCreate");
		writeTutorialProgress("knowledge-base-v1", "knowledgeSearch");
		clearTutorialProgress("agents-v1");
		expect(readTutorialProgress("agents-v1", ["agentsCreate"])).toBeNull();
		expect(readTutorialProgress("knowledge-base-v1", ["knowledgeSearch"])?.stepId).toBe("knowledgeSearch");
	});
});

describe("tutorial terminal state", () => {
	it("maps independent backend entries by persistence key", () => {
		prime([
			{ key: "main-app-v1", status: "completed" },
			{ key: "agents-v1", status: "skipped" },
		]);
		const { result } = renderHook(() => useTutorialState());
		expect(result.current.statusByKey).toEqual({ "main-app-v1": "completed", "agents-v1": "skipped" });
	});

	it("preserves completed when a replay is skipped", () => {
		prime([{ key: "agents-v1", status: "completed" }]);
		const { result } = renderHook(() => useTutorialState());
		result.current.markDone("agents-v1", "skipped");
		expect(mutateMock).not.toHaveBeenCalled();
	});

	it("promotes absent or skipped state to completed", () => {
		prime([{ key: "agents-v1", status: "skipped" }]);
		const { result } = renderHook(() => useTutorialState());
		result.current.markDone("agents-v1", "completed");
		expect(mutateMock).toHaveBeenCalledWith(
			{ body: { key: "agents-v1", status: "completed" } },
			expect.objectContaining({ onSuccess: expect.any(Function) }),
		);
	});
});
