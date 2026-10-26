// @vitest-environment jsdom

import { cleanup, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WelcomeTourDialog } from "@/features/onboarding/components/WelcomeTourDialog";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key, i18n: { changeLanguage: vi.fn() } }),
}));

beforeEach(() => {
	installJsdomEnvironmentMocks();
});

afterEach(cleanup);

describe("WelcomeTourDialog", () => {
	// The modal's default first focus is the title bar's close X, whose focus ring looked like an error on open.
	it("focuses the primary action when it opens", async () => {
		renderWithMantine(<WelcomeTourDialog opened={true} hasProgress={false} onStart={vi.fn()} onSkip={vi.fn()} />);

		await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId("onboarding-welcome-start")));
	});
});
