// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
	clearTourProgress,
	MAIN_APP_TOUR_KEY,
	readTourProgress,
	TOUR_PROGRESS_STORAGE_KEY,
	useTourState,
	writeTourProgress,
} from "@/features/onboarding/hooks/useTourState";

// Drives the generated GET hook's return so we can assert how shouldPrompt is derived from the persisted entries.
const { useQueryMock, useMutationMock, invalidateQueriesMock, mutateMock } = vi.hoisted(() => ({
	useQueryMock: vi.fn(),
	useMutationMock: vi.fn(),
	invalidateQueriesMock: vi.fn(),
	mutateMock: vi.fn(),
}));

vi.mock("@tanstack/react-query", () => ({
	useQuery: useQueryMock,
	useMutation: useMutationMock,
	useQueryClient: () => ({ invalidateQueries: invalidateQueriesMock }),
}));

// The generated options factories are not exercised here — only that useTourState passes their result through. Stub
// them so importing the hook doesn't pull the real generated client.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getTutorialStateOptions: () => ({ queryKey: ["getTutorialState"] }),
	getTutorialStateQueryKey: () => ["getTutorialState"],
	saveTutorialStateMutation: () => ({ mutationKey: ["saveTutorialState"] }),
}));

interface TutorialEntry {
	key?: string;
	status?: string;
	atUtc?: string;
}

function primeQuery(entries: TutorialEntry[] | undefined, overrides: { isSuccess?: boolean; isError?: boolean } = {}) {
	useQueryMock.mockReturnValue({
		data: entries === undefined ? undefined : { entries },
		isSuccess: overrides.isSuccess ?? true,
		isError: overrides.isError ?? false,
	});
	useMutationMock.mockReturnValue({ mutate: mutateMock });
}

afterEach(() => {
	vi.clearAllMocks();
});

describe("useTourState", () => {
	it("prompts when the GET returns no entry for the main-app tour key", () => {
		primeQuery([]);

		const { result } = renderHook(() => useTourState());

		expect(result.current.shouldPrompt).toBe(true);
		expect(result.current.isResolved).toBe(true);
	});

	it("does not prompt when the main-app entry is completed", () => {
		primeQuery([{ key: MAIN_APP_TOUR_KEY, status: "completed", atUtc: "2026-06-24T00:00:00Z" }]);

		const { result } = renderHook(() => useTourState());

		expect(result.current.shouldPrompt).toBe(false);
	});

	it("does not prompt when the main-app entry is skipped", () => {
		primeQuery([{ key: MAIN_APP_TOUR_KEY, status: "skipped", atUtc: "2026-06-24T00:00:00Z" }]);

		const { result } = renderHook(() => useTourState());

		expect(result.current.shouldPrompt).toBe(false);
	});

	it("does not prompt while the persisted state has not resolved (purely additive — never gate on a pending read)", () => {
		primeQuery(undefined, { isSuccess: false, isError: false });

		const { result } = renderHook(() => useTourState());

		expect(result.current.shouldPrompt).toBe(false);
		expect(result.current.isResolved).toBe(false);
	});

	it("markDone upserts the entry by key and status, then invalidates the GET", () => {
		primeQuery([]);

		const { result } = renderHook(() => useTourState());
		result.current.markDone("skipped");

		expect(mutateMock).toHaveBeenCalledWith(
			{ body: { key: MAIN_APP_TOUR_KEY, status: "skipped" } },
			expect.objectContaining({ onSuccess: expect.any(Function) }),
		);
	});
});

describe("tour progress persistence (Bug B resume-on-reload)", () => {
	afterEach(() => {
		globalThis.localStorage?.removeItem(TOUR_PROGRESS_STORAGE_KEY);
	});

	it("returns null when no progress is stored", () => {
		expect(readTourProgress()).toBeNull();
	});

	it("round-trips a written index through localStorage", () => {
		writeTourProgress(4);
		expect(readTourProgress()).toBe(4);
		expect(globalThis.localStorage?.getItem(TOUR_PROGRESS_STORAGE_KEY)).toBe("4");
	});

	it("clear removes the stored index so a finished tour cannot resurrect", () => {
		writeTourProgress(2);
		clearTourProgress();
		expect(readTourProgress()).toBeNull();
	});

	it("returns null for a non-integer / negative stored value (defensive)", () => {
		globalThis.localStorage?.setItem(TOUR_PROGRESS_STORAGE_KEY, "not-a-number");
		expect(readTourProgress()).toBeNull();
		globalThis.localStorage?.setItem(TOUR_PROGRESS_STORAGE_KEY, "-1");
		expect(readTourProgress()).toBeNull();
	});

	it("uses a stable namespaced key bound to the tour key", () => {
		expect(TOUR_PROGRESS_STORAGE_KEY).toBe(`xe-onboarding-${MAIN_APP_TOUR_KEY}-step`);
	});
});
