// @vitest-environment jsdom

import { renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MAIN_APP_TOUR_KEY, useTourState } from "@/features/onboarding/hooks/useTourState";

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
