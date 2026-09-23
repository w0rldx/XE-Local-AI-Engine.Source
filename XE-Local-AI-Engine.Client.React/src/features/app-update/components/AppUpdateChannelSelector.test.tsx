// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { AppUpdateChannelSelector } from "@/features/app-update/components/AppUpdateChannelSelector";
import de from "@/locales/de.json";
import en from "@/locales/en.json";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const statusPath = localApiPath("app-update/status");
const channelPath = localApiPath("app-update/channel");

// The real generated SDK runs the real zod schema on every response here, so a fixture missing one of the
// required members is a 422 and an empty component — not a component defect. Keep all twelve members.
function statusResponse(overrides: Record<string, unknown> = {}): Record<string, unknown> {
	return {
		currentVersion: "0.1.0-rc.2",
		availableVersion: null,
		updateAvailable: false,
		isConfigured: true,
		isDesktop: true,
		checkStatus: "ready",
		lastCheckedUtc: 1_700_000_000_000,
		selectedChannel: "stable",
		defaultChannel: "stable",
		availableChannels: ["stable", "preview", "development"],
		recommendedVersion: null,
		availableChannel: null,
		...overrides,
	};
}

// The node's own behaviour: the PUT persists the channel and answers with the status it recomputed, so the next
// GET and the mutation response agree. `saved()` is what proves the endpoint was reached with the right body.
function stubStatus(overrides: Record<string, unknown> = {}): { saved: () => unknown; fetched: () => number } {
	let current = statusResponse(overrides);
	let observed: unknown;
	let fetches = 0;
	server.use(
		http.get(statusPath, () => {
			fetches += 1;
			return HttpResponse.json(current);
		}),
		http.put(channelPath, async ({ request }) => {
			observed = await request.json();
			current = { ...current, selectedChannel: (observed as { channel?: string }).channel ?? current["selectedChannel"] };
			return HttpResponse.json(current);
		}),
	);
	return { saved: () => observed, fetched: () => fetches };
}

function openChannelList(): HTMLElement {
	fireEvent.click(screen.getByTestId("app-update-channel-select"));
	return screen.getByRole("listbox", { name: "Update channel", hidden: true });
}

describe("AppUpdateChannelSelector", () => {
	afterEach(() => cleanup());

	it("offers every channel the node reports, with the stored one selected", async () => {
		stubStatus();
		renderWithProviders(<AppUpdateChannelSelector />);

		await screen.findByDisplayValue("Stable — Recommended");
		const listbox = openChannelList();
		expect(within(listbox).getByRole("option", { name: "Stable — Recommended", hidden: true })).toBeTruthy();
		expect(within(listbox).getByRole("option", { name: "Preview", hidden: true })).toBeTruthy();
		expect(within(listbox).getByRole("option", { name: "Development", hidden: true })).toBeTruthy();
	});

	it("says a development build is less stable than a preview build", async () => {
		stubStatus({ selectedChannel: "development" });
		renderWithProviders(<AppUpdateChannelSelector />);

		const description = await screen.findByTestId("app-update-channel-description");
		expect(description.textContent).toMatch(/less stable than preview and not tested for release/i);
	});

	it("changes the channel through the dedicated endpoint and shows the status it answers with", async () => {
		const stub = stubStatus();
		renderWithProviders(<AppUpdateChannelSelector />);

		await screen.findByDisplayValue("Stable — Recommended");
		fireEvent.click(within(openChannelList()).getByRole("option", { name: "Development", hidden: true }));

		await waitFor(() => expect(stub.saved()).toEqual({ channel: "development" }));
		await screen.findByDisplayValue("Development");
	});

	// `POST app-update/apply` is deliberately never declared: setupMswServer's unhandled-request guard fails this
	// test naming the URL if the channel change reaches it. The PUT assertion is the positive half, so the test
	// cannot pass by doing nothing at all.
	it("never applies an update when the channel changes", async () => {
		const stub = stubStatus();
		renderWithProviders(<AppUpdateChannelSelector />);

		await screen.findByDisplayValue("Stable — Recommended");
		fireEvent.click(within(openChannelList()).getByRole("option", { name: "Preview", hidden: true }));

		await waitFor(() => expect(stub.saved()).toEqual({ channel: "preview" }));
	});

	it("shows the recommended stable version when it differs from the installed one", async () => {
		stubStatus({ recommendedVersion: "0.2.0" });
		renderWithProviders(<AppUpdateChannelSelector />);

		const line = await screen.findByTestId("app-update-recommended-version");
		expect(line.textContent).toContain("0.2.0");
	});

	it("says nothing about a recommended version that is already installed", async () => {
		stubStatus({ recommendedVersion: "0.1.0-rc.2" });
		renderWithProviders(<AppUpdateChannelSelector />);

		await screen.findByDisplayValue("Stable — Recommended");
		expect(screen.queryByTestId("app-update-recommended-version")).toBeNull();
	});

	it("keeps the stored channel and explains the failure when the change is refused", async () => {
		stubStatus();
		server.use(http.put(channelPath, () => HttpResponse.json({ detail: "nope" }, { status: 500 })));
		renderWithProviders(<AppUpdateChannelSelector />);

		await screen.findByDisplayValue("Stable — Recommended");
		fireEvent.click(within(openChannelList()).getByRole("option", { name: "Development", hidden: true }));

		await screen.findByTestId("app-update-channel-error");
		// A refused change leaves the stored channel alone, so the control shows it rather than a value nothing holds.
		expect(screen.getByTestId("app-update-channel-select")).toHaveProperty("value", "Stable — Recommended");
	});

	it("renders nothing when the build has no update source", async () => {
		const stub = stubStatus({ isConfigured: false });
		renderWithProviders(<AppUpdateChannelSelector />);

		await waitFor(() => expect(stub.fetched()).toBeGreaterThan(0));
		await waitFor(() => expect(screen.queryByTestId("app-update-channel-select")).toBeNull());
	});
});

// Asserted as data rather than through a render, so the German half of the copy is a contract of its own and not
// only a key-parity check.
describe("channel copy", () => {
	it("ships the channel names and the development warning in both locales", () => {
		const enCopy = en.pages.about.appUpdate;
		const deCopy = de.pages.about.appUpdate;

		expect(enCopy.channels).toEqual({ stable: "Stable — Recommended", preview: "Preview", development: "Development" });
		expect(deCopy.channels).toEqual({ stable: "Stabil — Empfohlen", preview: "Vorschau", development: "Entwicklung" });
		expect(enCopy.channelNames).toEqual({ stable: "Stable", preview: "Preview", development: "Development" });
		expect(deCopy.channelNames).toEqual({ stable: "Stabil", preview: "Vorschau", development: "Entwicklung" });
		expect(enCopy.channelDescriptions.development).toMatch(/Less stable than Preview and not tested for release/);
		expect(deCopy.channelDescriptions.development).toMatch(
			/Weniger stabil als die Vorschau und nicht für eine Veröffentlichung getestet/,
		);
		// "(stable)" is the honest qualifier: the recommendation is the newest stable, which on Preview or
		// Development can be lower than the running version.
		expect(enCopy.recommendedVersion).toBe("Recommended (stable) version: {{version}}");
		expect(deCopy.recommendedVersion).toBe("Empfohlene Version (stabil): {{version}}");
		expect(enCopy.channelChangeError).toBe("Couldn't change the update channel. Please try again.");
		expect(deCopy.channelChangeError).toBe("Der Update-Kanal konnte nicht geändert werden. Bitte versuche es erneut.");
	});
});
