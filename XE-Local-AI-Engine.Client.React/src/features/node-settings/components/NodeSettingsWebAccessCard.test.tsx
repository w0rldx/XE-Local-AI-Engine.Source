// @vitest-environment jsdom

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { NodeSettingsWebAccessCard } from "@/features/node-settings/components/NodeSettingsWebAccessCard";
import { type NodeSettingsFieldsForm, toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";
import { renderWithProviders } from "@/test/RenderWithProviders";

// No react-i18next mock: the real en bundle resolves, so the copy asserted here is the shipped string, not the in-code
// default.
function renderCard(formOverrides: Partial<NodeSettingsFieldsForm> = {}, errors: Record<string, string> = {}) {
	const onChange = vi.fn();
	renderWithProviders(
		<NodeSettingsWebAccessCard
			form={{ ...toNodeSettingsFieldsForm(undefined), ...formOverrides }}
			errors={errors}
			onChange={onChange}
		/>,
	);
	return onChange;
}

function switchInput(): HTMLInputElement {
	const host = screen.getByTestId("node-settings-web-access-enabled");
	return (host.querySelector("input[type='checkbox']") ?? host) as HTMLInputElement;
}

describe("NodeSettingsWebAccessCard", () => {
	it("renders off with an empty, editable SearXNG URL and the disclosure", () => {
		renderCard();

		const card = screen.getByTestId("node-settings-web-access-card");
		expect(card.textContent).toContain("Web access");
		expect(card.textContent).toContain("Allow web search and page fetching");
		expect(card.textContent).toContain(
			"the model can send search queries to DuckDuckGo or SearXNG and download public web pages",
		);
		expect(card.textContent).toContain("Retrieved content is shown to you for review before the model sees it.");
		expect(card.textContent).toContain("Leave empty to use DuckDuckGo (best effort).");
		expect(switchInput().checked).toBe(false);
		const url = screen.getByLabelText("SearXNG URL") as HTMLInputElement;
		expect(url.value).toBe("");
		expect(url.disabled).toBe(false);
	});

	it("reports the switch and URL edits through onChange", () => {
		const onChange = renderCard();

		fireEvent.click(switchInput());
		fireEvent.change(screen.getByLabelText("SearXNG URL"), { target: { value: "http://localhost:8888" } });

		expect(onChange).toHaveBeenCalledWith("webAccessEnabled", true);
		expect(onChange).toHaveBeenCalledWith("webSearchSearxngUrl", "http://localhost:8888");
	});

	it("shows the URL validation error from the bundle", () => {
		renderCard({ webSearchSearxngUrl: "/search" }, { webSearchSearxngUrl: "url" });

		expect(screen.getByText("Enter a valid http:// or https:// URL.")).toBeTruthy();
	});
});
