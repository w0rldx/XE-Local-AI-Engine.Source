import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useReducer, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	deleteExternalProviderConnectionMutation,
	listExternalProviderConnectionsOptions,
	listExternalProviderConnectionsQueryKey,
	saveExternalProviderConnectionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { ExternalProviderConnectionEditor } from "@/features/cloud-providers/external/components/ExternalProviderConnectionEditor";
import {
	connectionToFormValues,
	createFormRowIds,
	type ExternalProviderConnectionsDto,
	type ExternalProviderFormState,
	errorMessage,
	formReducer,
	initialFormState,
	parseConnectionsConflict,
	toSaveRequestBody,
} from "@/features/cloud-providers/external/models/ExternalProviderFormState";
import {
	type EditorTarget,
	type ExternalProviderFormErrors,
	type ExternalProviderFormValues,
	shouldWarnLocalDeclaration,
	validateExternalProviderForm,
} from "@/features/cloud-providers/external/models/ExternalProviderModel";
import type { ExternalProviderTemplateId } from "@/features/cloud-providers/models/ProviderTemplates";

interface ExternalProviderPanelProps {
	readonly target: EditorTarget;
	readonly initialValues: ExternalProviderFormValues;
	readonly template?: ExternalProviderTemplateId;
	// A write lost the revision race (or `null` once a write succeeds). The 409 carries the whole configuration, so the
	// page renders that instead of refetching, and the notice explains why the editor's contents just changed.
	readonly onConflict: (notice: string | null) => void;
	readonly onClose: () => void;
}

function formStateFor(values: ExternalProviderFormValues): ExternalProviderFormState {
	const rowIds = createFormRowIds(values);
	return { ...initialFormState, values, modelRowIds: rowIds.models, headerRowIds: rowIds.headers };
}

// The editor for one `ext:` connection. The page keys it per opening, so every opening starts from its own values.
export function ExternalProviderPanel({ target, initialValues, template, onConflict, onClose }: ExternalProviderPanelProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const { data: connectionsData } = useQuery(listExternalProviderConnectionsOptions());

	const [formState, dispatch] = useReducer(formReducer, initialValues, formStateFor);
	const { values, touched, submitted, modelRowIds, headerRowIds, probe } = formState;
	const [editorTarget, setEditorTarget] = useState<EditorTarget>(target);

	const connections = useMemo(() => connectionsData?.connections ?? [], [connectionsData]);
	const revision = connectionsData?.revision;
	const storedConnection = useMemo(
		() => (!editorTarget.isNew ? connections.find((entry) => entry.id === editorTarget.connectionId) : undefined),
		[connections, editorTarget],
	);

	const errors = useMemo(
		() =>
			validateExternalProviderForm(
				values,
				editorTarget.isNew,
				storedConnection ? { baseUrl: storedConnection.baseUrl, hasApiKey: storedConnection.hasApiKey } : undefined,
			),
		[editorTarget, storedConnection, values],
	);
	const hasErrors = Object.keys(errors).length > 0;
	// Only show a field's error once the operator has been in it, or after a save attempt — the same restraint the
	// cloud-settings editor uses so a fresh form is not pre-reddened.
	//
	// `apiKey` is the exception: its only error is raised by an edit to the BASE URL, and Save is disabled while it
	// stands, so gating it on having visited the key field (or on a submit that can never fire) would hide the one
	// message that explains why saving is blocked. `headers` is shown at once for the same reason: a moved address
	// raises the secret-header error, and a fresh form has no rows to pre-redden.
	const visibleErrors = useMemo<ExternalProviderFormErrors>(
		() =>
			submitted
				? errors
				: (Object.fromEntries(
						Object.entries(errors).filter(
							([key]) => key === "apiKey" || key === "headers" || touched[key as keyof ExternalProviderFormValues],
						),
					) as ExternalProviderFormErrors),
		[errors, submitted, touched],
	);

	// Renders what the server says is stored right now. When the connection under edit survived the other writer's
	// change, the editor reloads it; when it did not, the editor closes rather than pointing at an id that is gone.
	//
	// A CREATE that conflicts is the same shape with a different meaning: the id the operator typed is already taken,
	// so what comes back is somebody else's stored connection under that id. The editor switches to EDITING it —
	// otherwise the id stays typeable and Delete stays hidden while the fields show a resource that does exist.
	const applyConflict = useCallback(
		(conflict: ExternalProviderConnectionsDto, notice: string) => {
			queryClient.setQueryData(listExternalProviderConnectionsQueryKey(), conflict);
			onConflict(notice);
			// While creating, the target's id is still blank — the id under edit is the one in the form.
			const editedId = editorTarget.isNew ? values.connectionId.trim() : editorTarget.connectionId;
			const current = (conflict.connections ?? []).find((entry) => entry.id === editedId);
			if (current) {
				if (editorTarget.isNew) {
					setEditorTarget({ connectionId: current.id, isNew: false });
				}
				const formValues = connectionToFormValues(current);
				dispatch({ type: "reset", values: formValues, rowIds: createFormRowIds(formValues) });
				return;
			}
			if (!editorTarget.isNew) {
				onClose();
			}
		},
		[editorTarget, onClose, onConflict, queryClient, values.connectionId],
	);

	const onWriteSuccess = useCallback(
		(result: ExternalProviderConnectionsDto, message: string) => {
			queryClient.setQueryData(listExternalProviderConnectionsQueryKey(), result);
			toast.success(message);
			onConflict(null);
			onClose();
		},
		[onClose, onConflict, queryClient],
	);

	const saveMutation = useMutation({
		...saveExternalProviderConnectionMutation(),
		onSuccess: (result: ExternalProviderConnectionsDto) =>
			onWriteSuccess(result, t("pages.externalProviders.saved", "Connection saved.")),
		onError: (error) => {
			const conflict = parseConnectionsConflict(error);
			if (conflict) {
				applyConflict(conflict, t("pages.externalProviders.saveConflict"));
				return;
			}
			// A 400 carries the store's own message about what is not storable; apiErrorMessage surfaces it verbatim.
			toast.error(errorMessage(error));
		},
	});

	const deleteMutation = useMutation({
		...deleteExternalProviderConnectionMutation(),
		onSuccess: (result: ExternalProviderConnectionsDto) =>
			onWriteSuccess(result, t("pages.externalProviders.deleted", "Connection deleted.")),
		onError: (error) => {
			const conflict = parseConnectionsConflict(error);
			if (conflict) {
				applyConflict(conflict, t("pages.externalProviders.deleteConflict"));
				return;
			}
			toast.error(errorMessage(error));
		},
	});

	const handleSave = (): void => {
		dispatch({ type: "submit" });
		// Dispatch submit unconditionally so the inline errors appear, but only fire the write when the form is
		// actually valid — the disabled Save button is the same rule, stated twice on purpose.
		if (hasErrors) {
			return;
		}
		saveMutation.mutate({
			path: { connectionId: values.connectionId.trim() },
			body: toSaveRequestBody(values, revision),
		});
	};

	const handleDelete = async (): Promise<void> => {
		if (editorTarget.isNew) {
			return;
		}
		const confirmed = await confirm({
			title: t("pages.externalProviders.deleteConfirmTitle", "Delete connection"),
			description: t("pages.externalProviders.deleteConfirmDescription", { name: values.displayName }),
			confirmationText: t("pages.externalProviders.editor.delete", "Delete connection"),
		});
		if (!confirmed) {
			return;
		}
		deleteMutation.mutate({
			path: { connectionId: editorTarget.connectionId },
			query: { expectedRevision: revision },
		});
	};

	return (
		<ExternalProviderConnectionEditor
			values={values}
			visibleErrors={visibleErrors}
			modelRowIds={modelRowIds}
			headerRowIds={headerRowIds}
			probe={probe}
			dispatch={dispatch}
			connection={{ isNew: editorTarget.isNew, hasApiKey: storedConnection?.hasApiKey ?? false }}
			status={{
				hasErrors,
				warnLocalDeclaration: shouldWarnLocalDeclaration(values),
				isSaving: saveMutation.isPending,
				isDeleting: deleteMutation.isPending,
			}}
			onSave={handleSave}
			onDelete={handleDelete}
			onCancel={onClose}
			template={editorTarget.isNew ? template : undefined}
		/>
	);
}
