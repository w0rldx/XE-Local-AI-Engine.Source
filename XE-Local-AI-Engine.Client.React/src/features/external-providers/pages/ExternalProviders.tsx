import { Alert, Group, Loader, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlugConnected } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useReducer, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	deleteExternalProviderConnectionMutation,
	listExternalProviderConnectionsOptions,
	listExternalProviderConnectionsQueryKey,
	saveExternalProviderConnectionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { ExternalProviderConnectionEditor } from "@/features/external-providers/components/ExternalProviderConnectionEditor";
import { ExternalProviderConnectionList } from "@/features/external-providers/components/ExternalProviderConnectionList";
import {
	connectionToFormValues,
	createModelRowIds,
	emptyFormValues,
	errorMessage,
	type ExternalProviderConnectionsDto,
	formReducer,
	initialFormState,
	parseConnectionsConflict,
	toSaveRequestBody,
} from "@/features/external-providers/models/ExternalProviderFormState";
import {
	type ExternalProviderFormErrors,
	type ExternalProviderFormValues,
	shouldWarnLocalDeclaration,
	validateExternalProviderForm,
} from "@/features/external-providers/models/ExternalProviderModel";

interface EditorTarget {
	readonly connectionId: string;
	readonly isNew: boolean;
}

export function ExternalProviders() {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const { confirm } = useConfirm();
	const {
		data: connectionsData,
		isLoading,
		error: listError,
	} = useQuery(withResponseValidation(listExternalProviderConnectionsOptions()));

	const [formState, dispatch] = useReducer(formReducer, initialFormState);
	const { values, touched, submitted, modelRowIds } = formState;
	const [editorTarget, setEditorTarget] = useState<EditorTarget | null>(null);
	// Set when a write loses the revision race. The 409 carries the whole configuration, so the page renders that
	// instead of refetching — and this notice explains why the editor's contents just changed under the operator.
	const [conflictNotice, setConflictNotice] = useState<string | null>(null);

	const connections = useMemo(() => connectionsData?.connections ?? [], [connectionsData]);
	const revision = connectionsData?.revision;
	const storedConnection = useMemo(
		() => (editorTarget && !editorTarget.isNew ? connections.find((entry) => entry.id === editorTarget.connectionId) : undefined),
		[connections, editorTarget],
	);

	const errors = useMemo(
		() => (editorTarget ? validateExternalProviderForm(values, editorTarget.isNew) : {}),
		[editorTarget, values],
	);
	const hasErrors = Object.keys(errors).length > 0;
	// Only show a field's error once the operator has been in it, or after a save attempt — the same restraint the
	// cloud-settings editor uses so a fresh form is not pre-reddened.
	const visibleErrors = useMemo<ExternalProviderFormErrors>(
		() =>
			submitted
				? errors
				: (Object.fromEntries(
						Object.entries(errors).filter(([key]) => touched[key as keyof ExternalProviderFormValues]),
					) as ExternalProviderFormErrors),
		[errors, submitted, touched],
	);

	const openEditor = useCallback((target: EditorTarget, formValues: ExternalProviderFormValues) => {
		setConflictNotice(null);
		setEditorTarget(target);
		dispatch({ type: "reset", values: formValues, rowIds: createModelRowIds(formValues) });
	}, []);

	const closeEditor = useCallback(() => {
		setEditorTarget(null);
		dispatch({ type: "reset", values: emptyFormValues, rowIds: createModelRowIds(emptyFormValues) });
	}, []);

	// Renders what the server says is stored right now. When the connection under edit survived the other writer's
	// change, the editor reloads it; when it did not, the editor closes rather than pointing at an id that is gone.
	const applyConflict = useCallback(
		(conflict: ExternalProviderConnectionsDto, notice: string) => {
			queryClient.setQueryData(listExternalProviderConnectionsQueryKey(), conflict);
			setConflictNotice(notice);
			const current = editorTarget
				? (conflict.connections ?? []).find((entry) => entry.id === editorTarget.connectionId)
				: undefined;
			if (current) {
				const formValues = connectionToFormValues(current);
				dispatch({ type: "reset", values: formValues, rowIds: createModelRowIds(formValues) });
				return;
			}
			if (editorTarget && !editorTarget.isNew) {
				setEditorTarget(null);
				dispatch({ type: "reset", values: emptyFormValues, rowIds: createModelRowIds(emptyFormValues) });
			}
		},
		[editorTarget, queryClient],
	);

	const onWriteSuccess = useCallback(
		(result: ExternalProviderConnectionsDto, message: string) => {
			queryClient.setQueryData(listExternalProviderConnectionsQueryKey(), result);
			toast.success(message);
			setConflictNotice(null);
			setEditorTarget(null);
			dispatch({ type: "reset", values: emptyFormValues, rowIds: createModelRowIds(emptyFormValues) });
		},
		[queryClient],
	);

	const saveMutation = useMutation({
		...withResponseValidation(saveExternalProviderConnectionMutation()),
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
		...withResponseValidation(deleteExternalProviderConnectionMutation()),
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
		if (!editorTarget || hasErrors) {
			return;
		}
		saveMutation.mutate({
			path: { connectionId: values.connectionId.trim() },
			body: toSaveRequestBody(values, revision),
		});
	};

	const handleDelete = async (): Promise<void> => {
		if (!editorTarget || editorTarget.isNew) {
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

	const isWriting = saveMutation.isPending || deleteMutation.isPending;

	return (
		<PageShell>
			<PageHeader
				title={t("pages.externalProviders.title", "External providers")}
				icon={<IconPlugConnected size={24} />}
				subtitle={t("pages.externalProviders.subtitle")}
			/>

			{isLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.externalProviders.loading", "Loading connections…")}</Text>
				</Group>
			) : null}

			{listError ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />}>
					{errorMessage(listError)}
				</Alert>
			) : null}

			{conflictNotice !== null ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="external-provider-conflict">
					<Text size="sm">{conflictNotice}</Text>
				</Alert>
			) : null}

			<ExternalProviderConnectionList
				connections={connections}
				disabled={isWriting}
				onAdd={() => openEditor({ connectionId: "", isNew: true }, emptyFormValues)}
				onEdit={(connectionId) => {
					const connection = connections.find((entry) => entry.id === connectionId);
					if (connection) {
						openEditor({ connectionId, isNew: false }, connectionToFormValues(connection));
					}
				}}
			/>

			{editorTarget ? (
				<ExternalProviderConnectionEditor
					values={values}
					visibleErrors={visibleErrors}
					modelRowIds={modelRowIds}
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
					onCancel={closeEditor}
				/>
			) : null}
		</PageShell>
	);
}
