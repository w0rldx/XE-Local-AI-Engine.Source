// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
	clearTutorialProgress,
	readTutorialProgress,
	tutorialProgressStorageKey,
	useTutorialState,
	writeTutorialProgress,
} from "@/features/onboarding/hooks/useTourState";

const { useQueryMock, useMutationMock, mutateAsyncMock } = vi.hoisted(() => ({
	useQueryMock: vi.fn(),
	useMutationMock: vi.fn(),
	mutateAsyncMock: vi.fn(),
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
	useMutationMock.mockReturnValue({ mutateAsync: mutateAsyncMock });
	mutateAsyncMock.mockResolvedValue(undefined);
}

function createDeferred() {
	let resolvePromise: (() => void) | undefined;
	let rejectPromise: ((reason: unknown) => void) | undefined;
	const promise = new Promise<void>((resolve, reject) => {
		resolvePromise = resolve;
		rejectPromise = reject;
	});
	return {
		promise,
		resolve: () => resolvePromise?.(),
		reject: (reason: unknown) => rejectPromise?.(reason),
	};
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
	it("serializes terminal-state mutations through one shared scope", () => {
		prime([]);
		renderHook(() => useTutorialState());
		expect(useMutationMock).toHaveBeenCalledWith(expect.objectContaining({ scope: { id: "tutorial-state" } }));
	});

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
		expect(mutateAsyncMock).not.toHaveBeenCalled();
	});

	it("promotes absent or skipped state to completed", async () => {
		prime([{ key: "agents-v1", status: "skipped" }]);
		const { result } = renderHook(() => useTutorialState());
		result.current.markDone("agents-v1", "completed");
		expect(mutateAsyncMock).toHaveBeenCalledWith({ body: { key: "agents-v1", status: "completed" } });
		await waitFor(() => expect(mutateAsyncMock).toHaveBeenCalledOnce());
	});

	it("delivers each consecutive mutation result to its own callbacks", async () => {
		prime([]);
		const first = createDeferred();
		const second = createDeferred();
		mutateAsyncMock.mockReset();
		mutateAsyncMock.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
		const firstSuccess = vi.fn();
		const firstError = vi.fn();
		const secondSuccess = vi.fn();
		const secondError = vi.fn();
		const { result } = renderHook(() => useTutorialState());

		act(() => {
			result.current.markDone("quick-start", "completed", { onSuccess: firstSuccess, onError: firstError });
			result.current.markDone("agents-basics", "skipped", { onSuccess: secondSuccess, onError: secondError });
		});
		second.resolve();
		await waitFor(() => expect(secondSuccess).toHaveBeenCalledOnce());
		first.reject(new Error("first write failed"));
		await waitFor(() => expect(firstError).toHaveBeenCalledOnce());

		expect(firstSuccess).not.toHaveBeenCalled();
		expect(secondError).not.toHaveBeenCalled();
	});
});
