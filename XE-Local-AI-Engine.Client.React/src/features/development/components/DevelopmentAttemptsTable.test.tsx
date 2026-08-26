// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DevelopmentAttemptsTable } from "@/features/development/components/DevelopmentAttemptsTable";
import type { DevelopmentAttempt } from "@/features/development/models/DevelopmentModels";

vi.mock("react-i18next", () => ({ useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }) }));
afterEach(cleanup);
beforeEach(() => {
	globalThis.ResizeObserver = class {
		disconnect = vi.fn();
		observe = vi.fn();
		unobserve = vi.fn();
	};
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
	});
});

describe("DevelopmentAttemptsTable", () => {
	it("shows terminal diagnostics and the combined token count", () => {
		const attempt = {
			id: "attempt-1",
			role: "Reviewer",
			modelId: "model",
			provider: "Local",
			status: "Failed",
			inputTokens: 12,
			outputTokens: 8,
			predecessorAttemptId: "1234567890",
			terminalReason: "Validation failed.",
		} as DevelopmentAttempt;
		render(
			<MantineProvider>
				<DevelopmentAttemptsTable attempts={[attempt]} />
			</MantineProvider>,
		);
		expect(screen.getByText("20")).toBeTruthy();
		expect(screen.getByText("12345678")).toBeTruthy();
		expect(screen.getByTestId("development-attempt-reason-attempt-1").textContent).toContain("Validation failed.");
	});
});
