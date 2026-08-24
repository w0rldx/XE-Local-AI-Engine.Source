// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AgentOption } from "@/features/chat/models/ChatModels";
import { CreateWorkSessionDialog } from "@/features/workSessions/components/CreateWorkSessionDialog";
import { renderWithProviders } from "@/test/RenderWithProviders";

const agentId = "bbbbbbbb-0000-4000-8000-000000000002";
const agentOptions: AgentOption[] = [
	{ id: agentId, name: "Work Session — Research", description: "Research persona", kind: "Single", modelProfile: null, playbookEnabled: false },
];

function render(onSubmit = vi.fn()) {
	renderWithProviders(
		<CreateWorkSessionDialog opened={true} agentOptions={agentOptions} isSubmitting={false} onClose={vi.fn()} onSubmit={onSubmit} />,
	);
	return onSubmit;
}

describe("CreateWorkSessionDialog", () => {
	afterEach(() => {
		cleanup();
	});

	it("keeps submit disabled until a title, an objective and an agent are all present", async () => {
		render();
		const submit = screen.getByTestId("create-work-session-submit") as HTMLButtonElement;
		expect(submit.disabled).toBe(true);

		fireEvent.change(screen.getByTestId("create-work-session-title"), { target: { value: "Survey" } });
		expect(submit.disabled).toBe(true);
		fireEvent.change(screen.getByTestId("create-work-session-objective"), { target: { value: "Compare the options" } });
		// Still disabled: the agent is what the session pins, so it is not optional.
		expect(submit.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));
		fireEvent.click(await screen.findByTestId(`chat-agent-selector-option-${agentId}`));
		await waitFor(() => expect(submit.disabled).toBe(false));
	});

	it("defaults the kind to General and submits the trimmed values", async () => {
		const onSubmit = render();

		fireEvent.change(screen.getByTestId("create-work-session-title"), { target: { value: "  Survey  " } });
		fireEvent.change(screen.getByTestId("create-work-session-objective"), { target: { value: "  Compare the options  " } });
		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));
		fireEvent.click(await screen.findByTestId(`chat-agent-selector-option-${agentId}`));

		const submit = screen.getByTestId("create-work-session-submit") as HTMLButtonElement;
		await waitFor(() => expect(submit.disabled).toBe(false));
		fireEvent.click(submit);

		expect(onSubmit).toHaveBeenCalledWith({
			title: "Survey",
			objective: "Compare the options",
			kind: "General",
			agentDefinitionId: agentId,
		});
	});

	it("offers exactly the two v1 kinds — Development is reserved, not selectable", () => {
		render();
		const kinds = screen.getByTestId("create-work-session-kind");
		expect(kinds.textContent).toContain("General");
		expect(kinds.textContent).toContain("Research");
		expect(kinds.textContent).not.toContain("Development");
	});

	it("surfaces a create failure inside the dialog", () => {
		renderWithProviders(
			<CreateWorkSessionDialog
				opened={true}
				agentOptions={agentOptions}
				isSubmitting={false}
				errorMessage="the agent is not tool-capable"
				onClose={vi.fn()}
				onSubmit={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("create-work-session-error").textContent).toContain("the agent is not tool-capable");
	});
});
