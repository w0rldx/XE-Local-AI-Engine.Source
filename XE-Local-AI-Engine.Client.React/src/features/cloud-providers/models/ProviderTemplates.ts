import { emptyCustomHeader } from "@/core/http-headers/CustomHeaders";
import { emptyFormValues } from "@/features/cloud-providers/external/models/ExternalProviderFormState";
import type { ExternalProviderFormValues } from "@/features/cloud-providers/external/models/ExternalProviderModel";

// The three ways an `ext:` connection starts. They differ only in the form they pre-fill and the hints the editor
// shows; every one saves through the same connection store.
export type ExternalProviderTemplateId = "generic" | "gateway" | "azureOpenAi";

export type ProviderTemplateId = ExternalProviderTemplateId | "azureFoundry" | "codex";

// Why a template is offered but cannot be picked. Azure Foundry and Codex each hold one slot on the node.
export type ProviderTemplateBlock = "azureFoundryConfigured" | "codexSignedIn";

export interface ProviderTemplateOption {
	readonly id: ProviderTemplateId;
	readonly blockedBy: ProviderTemplateBlock | null;
}

export interface ProviderTemplateCapabilities {
	readonly cloudSettings: boolean;
	readonly externalProviders: boolean;
}

export interface ProviderSlots {
	readonly azureFoundryConfigured: boolean;
	readonly codexSignedIn: boolean;
}

/** The templates the "Add provider" picker offers: only those whose capability is on, in display order. */
export function providerTemplateOptions(
	capabilities: ProviderTemplateCapabilities,
	slots: ProviderSlots,
): ProviderTemplateOption[] {
	const options: ProviderTemplateOption[] = [];
	if (capabilities.externalProviders) {
		options.push({ id: "generic", blockedBy: null }, { id: "gateway", blockedBy: null }, { id: "azureOpenAi", blockedBy: null });
	}
	if (capabilities.cloudSettings) {
		options.push(
			{ id: "azureFoundry", blockedBy: slots.azureFoundryConfigured ? "azureFoundryConfigured" : null },
			{ id: "codex", blockedBy: slots.codexSignedIn ? "codexSignedIn" : null },
		);
	}
	return options;
}

export function isExternalProviderTemplate(id: ProviderTemplateId): id is ExternalProviderTemplateId {
	return id === "generic" || id === "gateway" || id === "azureOpenAi";
}

// Placeholders only: the operator's host, path and resource name are never guessed into the form.
export const externalTemplateBaseUrlPlaceholder: Partial<Record<ExternalProviderTemplateId, string>> = {
	gateway: "https://<host>/…/openai/v1",
	azureOpenAi: "https://<resource>.openai.azure.com/openai/v1",
};

/** The form a new connection opens with for a template. Every template starts declared Cloud, as the empty form does. */
export function externalTemplateValues(id: ExternalProviderTemplateId): ExternalProviderFormValues {
	// One non-secret project-id header row beside the bearer token, the name the known enterprise gateways use; the operator
	// fills in the value, and can rename or remove the row for a gateway that wants another header.
	return id === "gateway" ? { ...emptyFormValues, headers: [{ ...emptyCustomHeader, name: "project-id" }] } : emptyFormValues;
}
