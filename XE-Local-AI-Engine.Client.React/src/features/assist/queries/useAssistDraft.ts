import { useMutation } from "@tanstack/react-query";

import { draftAgentDefinition, draftSkill } from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import type { AssistDraft, AssistMode, AssistSurface } from "@/features/assist/models/AssistModels";

// Variables for one draft attempt. `signal` rides straight into the generated SDK call (which forwards it to axios),
// so aborting the caller's AbortController cancels the in-flight request — that is the dialog's Cancel button.
export interface AssistDraftVariables {
	mode: AssistMode;
	modelName: string;
	brief: string;
	existingName?: string;
	existingDescription?: string;
	existingContent?: string;
	signal: AbortSignal;
}

/**
 * The draft mutation for one surface. Calls the generated SDK fn imperatively (the `useLoadedModels` pattern) rather
 * than through the generated `*Mutation()` factory because the two endpoints answer with differently-named long
 * fields — `instructions` vs `body` — and normalizing them here keeps the surface split out of the dialog.
 *
 * Drafting never persists (plan invariant 2), so nothing is invalidated on success.
 */
export function useAssistDraft(surface: AssistSurface) {
	return useMutation<AssistDraft, Error, AssistDraftVariables>({
		mutationFn: async ({ signal, ...body }) => {
			if (surface === "agent") {
				const { data } = await callWithResponseValidation(draftAgentDefinition({ body, signal, throwOnError: true }));
				return {
					name: data.name,
					description: data.description,
					content: data.instructions,
					generationMetadata: data.generationMetadata,
				};
			}

			const { data } = await callWithResponseValidation(draftSkill({ body, signal, throwOnError: true }));
			return {
				name: data.name,
				description: data.description,
				content: data.body,
				generationMetadata: data.generationMetadata,
			};
		},
		// A local generation takes minutes and can fail for a reason a retry cannot fix (busy node, unparseable
		// output); silently re-running it would occupy the node's single draft slot again.
		retry: false,
	});
}
