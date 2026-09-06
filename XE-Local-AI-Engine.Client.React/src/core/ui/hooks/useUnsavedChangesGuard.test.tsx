// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, renderHook, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";

// t() returns the default value (2nd arg) so assertions don't depend on the real i18n bundle,
// matching the convention used across the React test suite.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
	}),
}));

// Controllable stand-in for TanStack Router's useBlocker. The hook under test owns the
// confirm-driving + proceed/reset logic; the router's own blocking is the library's concern.
const { blockerMock, proceedMock, resetMock } = vi.hoisted(() => ({
	blockerMock: vi.fn(),
	proceedMock: vi.fn(),
	resetMock: vi.fn(),
}));

vi.mock("@tanstack/react-router", () => ({
	useBlocker: blockerMock,
}));

type BlockerState =
	| { status: "idle"; proceed: undefined; reset: undefined }
	| { status: "blocked"; proceed: () => void; reset: () => void };

const idleState: BlockerState = { status: "idle", proceed: undefined, reset: undefined };
const blockedState: BlockerState = { status: "blocked", proceed: proceedMock, reset: resetMock };

function setBlockerState(state: BlockerState) {
	blockerMock.mockReturnValue(state);
}

function makeWrapper() {
	return function Wrapper({ children }: { children: ReactNode }) {
		return (
			<MantineProvider>
				<ConfirmProvider>{children}</ConfirmProvider>
			</MantineProvider>
		);
	};
}

describe("useUnsavedChangesGuard", () => {
	beforeEach(() => {
		blockerMock.mockReset();
		proceedMock.mockReset();
		resetMock.mockReset();
		setBlockerState(idleState);

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

	it("disables the blocker and never confirms when not dirty", () => {
		renderHook(() => useUnsavedChangesGuard({ isDirty: false }), { wrapper: makeWrapper() });

		expect(blockerMock).toHaveBeenCalledWith(
			expect.objectContaining({
				withResolver: true,
				disabled: true,
				enableBeforeUnload: false,
			}),
		);
		expect(screen.queryByRole("dialog")).toBeNull();
		expect(proceedMock).not.toHaveBeenCalled();
		expect(resetMock).not.toHaveBeenCalled();
	});

	it("enables the blocker (and beforeunload) when dirty", () => {
		setBlockerState(idleState);
		renderHook(() => useUnsavedChangesGuard({ isDirty: true }), { wrapper: makeWrapper() });

		expect(blockerMock).toHaveBeenCalledWith(
			expect.objectContaining({
				withResolver: true,
				disabled: false,
				enableBeforeUnload: true,
			}),
		);
		// shouldBlockFn reports the current dirty state.
		const opts = blockerMock.mock.calls.at(-1)?.[0] as { shouldBlockFn: () => boolean };
		expect(opts.shouldBlockFn()).toBe(true);
		// Idle status => no prompt yet.
		expect(screen.queryByRole("dialog")).toBeNull();
	});

	// A page that keeps its selection in search params navigates on every click. Without the opt-in those same-route
	// moves are blocked like a real departure, and the operator is asked to discard work to select something.
	it("with allowSameRoute, blocks a pathname change but not a search-param change", () => {
		renderHook(() => useUnsavedChangesGuard({ isDirty: true, allowSameRoute: true }), { wrapper: makeWrapper() });

		const opts = blockerMock.mock.calls.at(-1)?.[0] as { shouldBlockFn: (args: unknown) => boolean };

		expect(opts.shouldBlockFn({ current: { pathname: "/graph-workflows" }, next: { pathname: "/graph-workflows" } })).toBe(false);
		expect(opts.shouldBlockFn({ current: { pathname: "/graph-workflows" }, next: { pathname: "/chat" } })).toBe(true);
	});

	it("without allowSameRoute, blocks a same-route move too — the default is unchanged", () => {
		renderHook(() => useUnsavedChangesGuard({ isDirty: true }), { wrapper: makeWrapper() });

		const opts = blockerMock.mock.calls.at(-1)?.[0] as { shouldBlockFn: (args: unknown) => boolean };

		expect(opts.shouldBlockFn({ current: { pathname: "/agents" }, next: { pathname: "/agents" } })).toBe(true);
	});

	it("shows the confirm dialog when a blocked transition occurs", async () => {
		setBlockerState(blockedState);
		renderHook(() => useUnsavedChangesGuard({ isDirty: true }), { wrapper: makeWrapper() });

		const dialog = await screen.findByRole("dialog");
		expect(dialog).toBeTruthy();
		expect(screen.getByText("You have unsaved changes. Discard them and leave?")).toBeTruthy();
		expect(screen.getByRole("button", { name: "Discard" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Keep editing" })).toBeTruthy();
	});

	it("proceeds (discards) when the user confirms", async () => {
		setBlockerState(blockedState);
		renderHook(() => useUnsavedChangesGuard({ isDirty: true }), { wrapper: makeWrapper() });

		await screen.findByRole("dialog");
		fireEvent.click(screen.getByRole("button", { name: "Discard" }));

		await waitFor(() => expect(proceedMock).toHaveBeenCalledTimes(1));
		expect(resetMock).not.toHaveBeenCalled();
	});

	it("resets (keeps editing) when the user cancels", async () => {
		setBlockerState(blockedState);
		renderHook(() => useUnsavedChangesGuard({ isDirty: true }), { wrapper: makeWrapper() });

		await screen.findByRole("dialog");
		fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));

		await waitFor(() => expect(resetMock).toHaveBeenCalledTimes(1));
		expect(proceedMock).not.toHaveBeenCalled();
	});

	it("opens the confirm only once per blocked transition (no double-fire on re-render)", async () => {
		setBlockerState(blockedState);
		const { rerender } = renderHook(() => useUnsavedChangesGuard({ isDirty: true }), {
			wrapper: makeWrapper(),
		});

		await screen.findByRole("dialog");

		// Re-render while still blocked: the prompt must not stack a second dialog.
		rerender();

		await waitFor(() => {
			expect(screen.getAllByRole("dialog")).toHaveLength(1);
		});
	});
});
