// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";

// Mock the generated hey-api functions before importing the hooks under test.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAppUpdateStatusOptions: vi.fn(),
	// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
	getAppUpdateStatusQueryKey: vi.fn(() => [{ _id: "getAppUpdateStatus" }]),
	getGitHubAuthStatusOptions: vi.fn(),
	// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
	getGitHubAuthStatusQueryKey: vi.fn(() => [{ _id: "getGitHubAuthStatus" }]),
	startGitHubAuthMutation: vi.fn(),
	pollGitHubAuthMutation: vi.fn(),
	signOutGitHubAuthMutation: vi.fn(),
	applyAppUpdateMutation: vi.fn(),
}));

// Minimal stub for withResponseValidation: passes through the options object unchanged.
vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (opts: unknown) => opts,
}));

import {
	getAppUpdateStatusOptions,
	getGitHubAuthStatusOptions,
	pollGitHubAuthMutation,
	startGitHubAuthMutation,
	signOutGitHubAuthMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";

import {
	getAppUpdateStatusQueryKey,
	useAppUpdateStatus,
	useGitHubAuthStatus,
	usePollGitHubAuth,
	useRefreshAppUpdateStatus,
	useSignOutGitHubAuth,
	useStartGitHubAuth,
} from "./useAppUpdate";

const statusMock = vi.mocked(getAppUpdateStatusOptions);
const authStatusMock = vi.mocked(getGitHubAuthStatusOptions);
const startMutationFn = vi.fn();
const pollMutationFn = vi.fn();
const signOutMutationFn = vi.fn();

vi.mocked(startGitHubAuthMutation).mockReturnValue({ mutationFn: startMutationFn } as never);
vi.mocked(pollGitHubAuthMutation).mockReturnValue({ mutationFn: pollMutationFn } as never);
vi.mocked(signOutGitHubAuthMutation).mockReturnValue({ mutationFn: signOutMutationFn } as never);

function makeWrapper() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper, queryClient };
}

describe("useAppUpdateStatus", () => {
	beforeEach(() => {
		statusMock.mockReturnValue({
			queryKey: [
				// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
				{ _id: "getAppUpdateStatus" },
			],
			queryFn: async () => ({
				isDesktop: true,
				authState: "signedIn",
				updateAvailable: true,
				currentVersion: "1.0.0",
				availableVersion: "1.1.0",
			}),
		} as never);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("returns update status from the server", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useAppUpdateStatus(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.updateAvailable).toBe(true);
		expect(result.current.data?.isDesktop).toBe(true);
		expect(result.current.data?.authState).toBe("signedIn");
	});
});

describe("useRefreshAppUpdateStatus", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("forces a refresh:true server check and seeds the default cache", async () => {
		const refreshedSnapshot = {
			isDesktop: true,
			authState: "signedIn",
			updateAvailable: true,
			currentVersion: "1.0.0",
			availableVersion: "1.1.0",
		};

		// Return a real query-options object whose key reflects the refresh flag so fetchQuery + setQueryData work.
		statusMock.mockImplementation(
			(opts) =>
				({
					queryKey: [
						// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
						{ _id: "getAppUpdateStatus", query: opts?.query },
					],
					queryFn: async () => refreshedSnapshot,
				}) as never,
		);
		vi.mocked(getAppUpdateStatusQueryKey).mockImplementation(
			(opts) =>
				[
					// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
					{ _id: "getAppUpdateStatus", query: opts?.query },
				] as never,
		);

		const { Wrapper, queryClient } = makeWrapper();
		const { result } = renderHook(() => useRefreshAppUpdateStatus(), { wrapper: Wrapper });

		result.current.mutate();

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// The forced check must request refresh:true (the default query would only re-serve the cached snapshot).
		expect(statusMock).toHaveBeenCalledWith({ query: { refresh: true } });
		// The fresh snapshot must be written into the default (refresh:null) cache the visible status reads from.
		expect(queryClient.getQueryData(getAppUpdateStatusQueryKey({ query: { refresh: null } }))).toEqual(
			refreshedSnapshot,
		);
	});
});

describe("useGitHubAuthStatus", () => {
	beforeEach(() => {
		authStatusMock.mockReturnValue({
			queryKey: [
				// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
				{ _id: "getGitHubAuthStatus" },
			],
			queryFn: async () => ({ authState: "signedOut", login: null }),
		} as never);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("returns signedOut when no token is stored", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useGitHubAuthStatus(), { wrapper: Wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.authState).toBe("signedOut");
	});
});

describe("useStartGitHubAuth", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("returns userCode and verificationUri from startGitHubAuth", async () => {
		startMutationFn.mockResolvedValue({
			userCode: "ABCD-1234",
			verificationUri: "https://github.com/login/device",
			expiresInSeconds: 900,
			intervalSeconds: 5,
		});

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useStartGitHubAuth(), { wrapper: Wrapper });

		// Variables don't matter because the mutationFn is mocked — use an empty object.
		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.userCode).toBe("ABCD-1234");
		expect(result.current.data?.verificationUri).toBe("https://github.com/login/device");
		// device_code must never appear in the response consumed by React.
		expect(result.current.data).not.toHaveProperty("deviceCode");
		expect(result.current.data).not.toHaveProperty("device_code");
	});
});

describe("usePollGitHubAuth", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("returns authorized state when poll succeeds", async () => {
		pollMutationFn.mockResolvedValue({ state: "authorized", login: "octocat" });

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePollGitHubAuth(), { wrapper: Wrapper });

		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.state).toBe("authorized");
	});

	it("returns pending state while device flow is still in progress", async () => {
		pollMutationFn.mockResolvedValue({ state: "pending" });

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => usePollGitHubAuth(), { wrapper: Wrapper });

		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(result.current.data?.state).toBe("pending");
	});
});

describe("useSignOutGitHubAuth", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("calls signOutGitHubAuth mutation", async () => {
		signOutMutationFn.mockResolvedValue(undefined);

		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSignOutGitHubAuth(), { wrapper: Wrapper });

		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(signOutMutationFn).toHaveBeenCalledOnce();
	});
});
