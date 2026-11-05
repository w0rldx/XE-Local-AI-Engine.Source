// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { HeaderBar } from "@/core/layout/components/HeaderBar/HeaderBar";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The drawer pulls in the whole navigation model; this test is about the bar's own slots.
vi.mock("@/core/layout/components/MobileNavigationBar/MobileNavigationBar", () => ({ MobileNavigationBar: () => null }));

const drawerActions = { reportProblem: async () => undefined, reportProblemPending: false, openAbout: () => undefined };

function setViewport(width: number, height: number): () => void {
	const originalWidth = window.innerWidth;
	const originalHeight = window.innerHeight;
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
	Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: height });
	return () => {
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: originalWidth });
		Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: originalHeight });
	};
}

afterEach(cleanup);

describe("HeaderBar", () => {
	// The operations indicator is how a first-run runtime failure stays visible outside Home, so no screen size may drop it.
	it.each([
		[390, 844, false],
		[1280, 720, true],
	])("at %i×%i renders the indicator, and the status area only on desktop (%s)", async (width, height, desktop) => {
		const restore = setViewport(width, height);
		try {
			renderWithProviders(
				<HeaderBar
					headerIndicator={<span data-testid="indicator" />}
					headerStatus={<span data-testid="status" />}
					headerActions={null}
					drawerActions={drawerActions}
				/>,
				{ route: "/chat" },
			);

			expect(await screen.findByTestId("indicator")).toBeTruthy();
			expect(screen.queryByTestId("status") !== null).toBe(desktop);
		} finally {
			restore();
		}
	});
});
