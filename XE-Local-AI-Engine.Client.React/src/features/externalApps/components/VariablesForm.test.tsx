// @vitest-environment jsdom

// The one mechanic worth pinning here is the secret's: a stored secret arrives as the sentinel, its box renders EMPTY,
// and an untouched form sends the sentinel back unchanged. Get that wrong and a save silently overwrites a stored
// secret with the literal mask — a failure nothing else in the stack catches.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import { VariablesForm } from "@/features/externalApps/components/VariablesForm";
import { EXTERNAL_APP_SECRET_SENTINEL } from "@/features/externalApps/models/ExternalAppModels";
import type { ExternalAppVariableValues } from "@/features/externalApps/models/ExternalAppVariables";
import { externalAppVariable } from "@/features/externalApps/test/ExternalAppFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderForm(
	definitions: readonly ReturnType<typeof externalAppVariable>[],
	values: ExternalAppVariableValues,
	onChange = vi.fn(),
	storedSecrets: readonly string[] = [],
) {
	const result = renderWithProviders(
		<VariablesForm definitions={definitions} values={values} issues={[]} storedSecrets={storedSecrets} onChange={onChange} />,
	);
	return { ...result, onChange };
}

// A stateful host for the one stored secret, so an edit reaches the box the way it does in the app.
function renderSecretHost() {
	const definitions = [externalAppVariable({ name: "token", label: "Token", type: "secret" })];
	function Host() {
		const [values, setValues] = useState<ExternalAppVariableValues>({ token: EXTERNAL_APP_SECRET_SENTINEL });
		return (
			<>
				<VariablesForm
					definitions={definitions}
					values={values}
					issues={[]}
					storedSecrets={["token"]}
					onChange={(name, value) => setValues((previous) => ({ ...previous, [name]: value }))}
				/>
				<output data-testid="submitted">{values["token"]}</output>
			</>
		);
	}
	return renderWithProviders(<Host />);
}

// The same host for an ADVANCED secret: the Advanced section is a `Collapse keepMounted={false}`, so closing it
// UNMOUNTS the box. Whether the field has a stored secret therefore has to come from the parent, which is what
// `SettingsTab` and the update dialog read off the masked values the node answered with.
function renderAdvancedSecretHost() {
	const definitions = [
		externalAppVariable({ name: "basic", label: "Basic" }),
		externalAppVariable({ name: "token", label: "Token", type: "secret", advanced: true, required: false }),
	];
	function Host() {
		const [values, setValues] = useState<ExternalAppVariableValues>({
			basic: "",
			token: EXTERNAL_APP_SECRET_SENTINEL,
		});
		return (
			<>
				<VariablesForm
					definitions={definitions}
					values={values}
					issues={[]}
					storedSecrets={["token"]}
					onChange={(name, value) => setValues((previous) => ({ ...previous, [name]: value }))}
				/>
				<output data-testid="submitted">{values["token"]}</output>
			</>
		);
	}
	return renderWithProviders(<Host />);
}

describe("VariablesForm", () => {
	it("renders a control for each of the five variable types", () => {
		const definitions = [
			externalAppVariable({ name: "text", label: "Text", type: "string" }),
			externalAppVariable({ name: "token", label: "Token", type: "secret" }),
			externalAppVariable({ name: "port", label: "Port", type: "integer" }),
			externalAppVariable({ name: "enabled", label: "Enabled", type: "boolean" }),
			externalAppVariable({ name: "mode", label: "Mode", type: "enum", allowedValues: ["fast", "slow"] }),
		];
		renderForm(definitions, { text: "a", token: "", port: "80", enabled: "true", mode: "fast" });

		expect((screen.getByTestId("external-app-variable-text") as HTMLInputElement).value).toBe("a");
		expect(screen.getByTestId("external-app-variable-token")).toBeDefined();
		expect((screen.getByTestId("external-app-variable-port") as HTMLInputElement).value).toBe("80");
		expect((screen.getByTestId("external-app-variable-enabled") as HTMLInputElement).checked).toBe(true);
		expect((screen.getByTestId("external-app-variable-mode") as HTMLInputElement).value).toBe("fast");
	});

	it("hides advanced variables until the toggle is opened", async () => {
		const definitions = [
			externalAppVariable({ name: "basic", label: "Basic" }),
			externalAppVariable({ name: "tuning", label: "Tuning", advanced: true, required: false }),
		];
		renderForm(definitions, { basic: "", tuning: "" });

		expect(screen.getByTestId("external-app-variable-basic")).toBeDefined();
		expect(screen.queryByTestId("external-app-variable-tuning")).toBeNull();

		fireEvent.click(screen.getByTestId("external-app-variables-advanced-toggle"));
		// The section mounts as the collapse leaves its exited state, which is a transition tick, not a sleep.
		await waitFor(() => expect(screen.getByTestId("external-app-variable-tuning")).toBeDefined());
	});

	it("renders a stored secret empty with the stored placeholder and leaves its value untouched", () => {
		const definitions = [externalAppVariable({ name: "token", label: "Token", type: "secret" })];
		const values: ExternalAppVariableValues = { token: EXTERNAL_APP_SECRET_SENTINEL };
		const { onChange } = renderForm(definitions, values);

		const input = screen.getByTestId("external-app-variable-token") as HTMLInputElement;
		expect(input.value).toBe("");
		expect(input.placeholder).toBe("•••• stored — leave empty to keep");
		// Nothing was typed, so the parent's value is still the sentinel and that is what a save round-trips.
		expect(onChange).not.toHaveBeenCalled();
		expect(values["token"]).toBe(EXTERNAL_APP_SECRET_SENTINEL);
	});

	// The S5 live round found every `secret` variable rendering as a plain text box: the Odysseus install dialog
	// printed the admin password in clear as it was typed. The type is what has to drive the masking, so it is
	// asserted on the element rather than assumed from the control the component happens to pick.
	it("masks a secret as it is typed and leaves a string in the clear", () => {
		const definitions = [
			externalAppVariable({ name: "token", label: "Token", type: "secret" }),
			externalAppVariable({ name: "text", label: "Text", type: "string" }),
		];
		renderForm(definitions, { token: "hunter2", text: "plain" });

		const secret = screen.getByTestId("external-app-variable-token") as HTMLInputElement;
		expect(secret.type).toBe("password");
		expect(secret.value).toBe("hunter2");

		const plain = screen.getByTestId("external-app-variable-text") as HTMLInputElement;
		expect(plain.type).toBe("text");
	});

	it("replaces the sentinel when the operator types into a stored secret", () => {
		const definitions = [externalAppVariable({ name: "token", label: "Token", type: "secret" })];
		const { onChange } = renderForm(definitions, { token: EXTERNAL_APP_SECRET_SENTINEL });

		fireEvent.change(screen.getByTestId("external-app-variable-token"), { target: { value: "hunter2" } });

		expect(onChange).toHaveBeenCalledWith("token", "hunter2");
	});

	// C3: keep and clear used to differ only by an invisible sentinel, so typing into a stored secret and deleting it
	// again destroyed the stored password with nothing on screen saying so, and there was no deliberate way to clear
	// one. Both cases need the real host loop — `SettingsTab` and both dialogs hold the values in state — because a
	// controlled box only round-trips an edit once the parent has echoed it back.
	it("keeps the stored secret when its box is typed into and emptied again", () => {
		renderSecretHost();
		const input = screen.getByTestId("external-app-variable-token");

		fireEvent.change(input, { target: { value: "hunter2" } });
		expect(screen.getByTestId("submitted").textContent).toBe("hunter2");

		fireEvent.change(input, { target: { value: "" } });

		expect(screen.getByTestId("submitted").textContent).toBe(EXTERNAL_APP_SECRET_SENTINEL);
		expect(screen.queryByTestId("external-app-variable-token-cleared")).toBeNull();
	});

	it("reports an empty value and says so when the stored secret is cleared on purpose", () => {
		renderSecretHost();

		fireEvent.click(screen.getByTestId("external-app-variable-token-clear"));

		expect(screen.getByTestId("submitted").textContent).toBe("");
		expect(screen.getByTestId("external-app-variable-token-cleared").textContent).toBe(
			"This value will be removed when you save.",
		);

		fireEvent.click(screen.getByTestId("external-app-variable-token-undo"));

		expect(screen.getByTestId("submitted").textContent).toBe(EXTERNAL_APP_SECRET_SENTINEL);
		expect(screen.queryByTestId("external-app-variable-token-cleared")).toBeNull();
	});

	// Codex r2: the pending clear used to be remembered INSIDE the secret box, so collapsing the Advanced section
	// (`keepMounted={false}`, the box is unmounted) and reopening it dropped the warning and the Undo while the
	// parent still held "" — the next save deleted the credential with nothing on screen saying it would.
	it("keeps a cleared advanced secret marked as cleared after the section is collapsed and reopened", async () => {
		renderAdvancedSecretHost();

		fireEvent.click(screen.getByTestId("external-app-variables-advanced-toggle"));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-token")).toBeDefined());

		fireEvent.click(screen.getByTestId("external-app-variable-token-clear"));
		expect(screen.getByTestId("submitted").textContent).toBe("");
		expect(screen.getByTestId("external-app-variable-token-cleared")).toBeDefined();

		// Closing is a height transition, and the section is unmounted on its `transitionend` — the event jsdom never
		// sends by itself. Waiting for the collapsed height and then sending it is the real close, not a sleep.
		const section = screen.getByTestId("external-app-variables-advanced");
		fireEvent.click(screen.getByTestId("external-app-variables-advanced-toggle"));
		await waitFor(() => expect(section.style.height).toBe("0px"));
		fireEvent.transitionEnd(section, { propertyName: "height" });
		await waitFor(() => expect(screen.queryByTestId("external-app-variable-token")).toBeNull());

		fireEvent.click(screen.getByTestId("external-app-variables-advanced-toggle"));
		await waitFor(() => expect(screen.getByTestId("external-app-variable-token")).toBeDefined());

		// The rebuilt box still says the value will be removed, still offers the undo, and still carries "".
		expect(screen.getByTestId("external-app-variable-token-cleared").textContent).toBe(
			"This value will be removed when you save.",
		);
		expect(screen.getByTestId("external-app-variable-token-undo")).toBeDefined();
		expect(screen.getByTestId("submitted").textContent).toBe("");
	});

	it("reports a checkbox and an integer as strings", () => {
		const definitions = [
			externalAppVariable({ name: "enabled", label: "Enabled", type: "boolean", required: false }),
			externalAppVariable({ name: "port", label: "Port", type: "integer", required: false }),
		];
		const { onChange } = renderForm(definitions, { enabled: "false", port: "" });

		fireEvent.click(screen.getByTestId("external-app-variable-enabled"));
		expect(onChange).toHaveBeenCalledWith("enabled", "true");

		fireEvent.change(screen.getByTestId("external-app-variable-port"), { target: { value: "8080" } });
		expect(onChange).toHaveBeenCalledWith("port", "8080");
	});

	it("shows an issue as that field's error, translated from its own key", () => {
		const definitions = [externalAppVariable({ name: "baseUrl", label: "Base URL" })];
		renderWithProviders(
			<VariablesForm
				definitions={definitions}
				values={{ baseUrl: "" }}
				issues={[
					{
						name: "baseUrl",
						messageKey: "pages.externalApps.variables.issues.required",
						params: { label: "Base URL" },
					},
				]}
				storedSecrets={[]}
				onChange={vi.fn()}
			/>,
		);

		expect(screen.getByText("Base URL is required.")).toBeDefined();
	});

	it("says so when the application declares no settings at all", () => {
		renderForm([], {});

		expect(screen.getByText("This application needs no settings.")).toBeDefined();
	});
});
