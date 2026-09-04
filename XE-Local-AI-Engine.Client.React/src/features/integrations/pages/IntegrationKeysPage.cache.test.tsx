// @vitest-environment jsdom

// The show-once plaintext must live in the page's component state and nowhere else. The main page test mocks the key
// hooks, which replaces the very cache a leak would sit in, so this file drives the REAL hooks against a REAL
// QueryClient with MSW answering the wire — the only setup in which "not in the mutation cache" is an assertion
// rather than a restatement of the mock.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { IntegrationKeysPage } from "@/features/integrations/pages/IntegrationKeysPage";
import { useIntegrationsUiStore } from "@/features/integrations/stores/IntegrationsUiStore";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const PRINCIPAL_ID = "3f9c1a2b-0000-4000-8000-000000000001";
const KEY_ID = "5c7d2e3f-0000-4000-8000-000000000002";
const PLAINTEXT = "xeint_live_plaintext_value_never_cached";

const keyView = {
	id: KEY_ID,
	principalId: PRINCIPAL_ID,
	keyPrefix: "xeint_aaaa",
	label: "new-key",
	allowedTriggerIds: null,
	createdAtUtc: 1_700_000_000_000,
	lastUsedAtUtc: null,
	revokedAtUtc: null,
};

/** The three routes the page reads and writes, with the generate response carrying the show-once plaintext. */
function installRoutes(): void {
	server.use(
		http.get(localApiPath("integrations/keys"), () => HttpResponse.json({ items: [] })),
		http.get(localApiPath("integrations/triggers"), () => HttpResponse.json({ items: [] })),
		http.post(localApiPath("integrations/keys"), () => HttpResponse.json({ key: PLAINTEXT, view: keyView })),
	);
}

/**
 * Every cached value the client holds, as text. Mutation and query state are plain JSON by construction, so a
 * substring search over them is a complete answer to "is the plaintext still in a cache?".
 */
function cachedText(queryClient: ReturnType<typeof createTestQueryClient>): string {
	return JSON.stringify([
		queryClient.getMutationCache().getAll().map((mutation) => mutation.state),
		queryClient.getQueryCache().getAll().map((query) => query.state.data),
	]);
}

describe("IntegrationKeysPage plaintext lifetime", () => {
	beforeEach(() => {
		useIntegrationsUiStore.setState({ editorTarget: null, keyDialogOpen: false });
		installRoutes();
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the generated key once and leaves it in no cache", async () => {
		const queryClient = createTestQueryClient();
		renderWithProviders(
			<ConfirmProvider>
				<IntegrationKeysPage />
			</ConfirmProvider>,
			{ queryClient },
		);

		fireEvent.click(await screen.findByTestId("integration-key-generate-button"));
		fireEvent.change(await screen.findByTestId("integration-key-generate-label"), {
			target: { value: "new-key" },
		});
		fireEvent.click(screen.getByTestId("integration-key-generate-all-triggers"));
		fireEvent.click(screen.getByTestId("integration-key-generate-submit"));

		const revealed = await screen.findByTestId("integration-key-reveal-value");
		expect(screen.getAllByTestId("integration-key-reveal-value")).toHaveLength(1);
		expect(revealed.textContent).toBe(PLAINTEXT);

		// The capture resets the mutation and the hook's zero gc time collects the entry, so the wait is for the gc
		// timer rather than for anything the operator can see.
		await waitFor(() => {
			expect(queryClient.getMutationCache().getAll()).toHaveLength(0);
		});
		expect(cachedText(queryClient)).not.toContain(PLAINTEXT);

		// Losing the plaintext from the cache must not have taken it off the screen: the operator is still reading it.
		expect(screen.getByTestId("integration-key-reveal-value").textContent).toBe(PLAINTEXT);
	});
});
