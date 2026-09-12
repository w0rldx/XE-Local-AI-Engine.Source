import { Button, Group } from "@mantine/core";
import { useQueryClient } from "@tanstack/react-query";
import { useRef } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import {
	externalAppConflictMessageKey,
	isExternalAppStaleRowConflict,
	readExternalAppConflict,
} from "@/features/externalApps/api/ExternalAppConflict";
import type { ExternalAppInstanceView } from "@/features/externalApps/models/ExternalAppModels";
import {
	isExternalAppBusy,
	isExternalAppCancellable,
	toExternalAppStatus,
} from "@/features/externalApps/models/ExternalAppModels";
import {
	invalidateExternalAppInstance,
	useCancelExternalAppOperation,
	useResetExternalApp,
	useRestartExternalApp,
	useStartExternalApp,
	useStopExternalApp,
	useUninstallExternalApp,
} from "@/features/externalApps/queries/useExternalApps";

interface InstanceActionsProps {
	readonly instance: ExternalAppInstanceView;
	/** Update opens the dialog the page owns; this row never posts an update itself. */
	readonly onUpdate?: () => void;
	/** The installed table's subset: Open, Start and Stop. Restart, Update, Cancel and the destructive pair are the detail page's. */
	readonly compact?: boolean;
	readonly "data-testid"?: string;
}

const keyPrefix = "pages.externalApps.actions";

/**
 * Everything an operator can do to one installed application.
 *
 * Two rules run through every button. **Every lifecycle call carries `expectedVersion`** — the `version` this row
 * rendered — and a stale one comes back as a 409 that toasts and invalidates, so the next click carries a fresh
 * token; V1 has no idempotency keys and this is the whole concurrency contract. And **Cancel is the only action
 * offered while the instance is busy**: without it a mis-started multi-gigabyte pull cannot be stopped from the UI.
 */
export function InstanceActions({ instance, onUpdate, compact = false, "data-testid": testId }: InstanceActionsProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const queryClient = useQueryClient();

	const instanceId = instance.id ?? "";
	// NOT `?? 0`: the store seeds a row at version 0, so a guessed zero is a real token that would address SOMEONE's
	// row rather than being refused. The generated client types every member optional, so absence has to be handled.
	const expectedVersion = instance.version;
	// The confirmation is awaited, and a hub ping can re-read the row while it is open. The ref is what the resolved
	// promise reads, because the closure it resolves into captured the version this render produced.
	const latestVersion = useRef(expectedVersion);
	latestVersion.current = expectedVersion;
	const name = instance.displayName ?? instanceId;
	const status = toExternalAppStatus(instance.status);
	const busy = isExternalAppBusy(status);

	const start = useStartExternalApp();
	const stop = useStopExternalApp();
	const restart = useRestartExternalApp();
	const reset = useResetExternalApp();
	const uninstall = useUninstallExternalApp();
	const cancel = useCancelExternalAppOperation();

	// The single open target: the ONE published port the server composed a URL for. Never the first port — a sidecar
	// listed first commonly has no address at all.
	const openTarget = (instance.publishedPorts ?? []).find((port) => Boolean(port.url));

	const onError = (error: unknown): void => {
		const conflict = readExternalAppConflict(error);
		const messageKey = conflict ? externalAppConflictMessageKey(conflict.conflictType) : undefined;
		toast.error(messageKey ? t(messageKey) : apiErrorMessage(error, t(`${keyPrefix}.failed`)));
		// A stale row is the cause of both of these, so the next attempt has to be made against a re-read.
		if (isExternalAppStaleRowConflict(conflict?.conflictType)) {
			invalidateExternalAppInstance(queryClient, instanceId);
		}
	};

	const announce = (messageKey: string) => () => toast.success(t(`${keyPrefix}.${messageKey}`, { name }));

	/**
	 * The one gate every version-carrying action passes through. A row that arrived without a `version` cannot be
	 * addressed at all — sending a guess is a lost update against whichever row holds that token — so the click says so
	 * and sends nothing.
	 */
	const withVersion = (action: (version: number) => void) => (): void => {
		if (expectedVersion === undefined) {
			toast.error(t(`${keyPrefix}.versionMissing`, { name }));
			return;
		}
		action(expectedVersion);
	};

	/**
	 * The destructive pair only. The version is re-read when the confirmation RESOLVES, not when it opened: a hub
	 * refresh that lands while the dialog is up repaints the row but cannot reach the closure, so sending the captured
	 * token would post a guaranteed 409 — and, worse, would be destroying something other than what was described.
	 */
	const confirmThen = (kind: "resetConfirm" | "uninstallConfirm", action: (version: number) => void): void => {
		// Through the same gate as every other action, invoked immediately: a row with no version never opens a dialog
		// that it could not honour.
		withVersion((version) => {
			confirm({
				title: t(`${keyPrefix}.${kind}.title`, { name }),
				description: t(`${keyPrefix}.${kind}.description`),
				confirmationText: t(`${keyPrefix}.${kind}.confirm`),
			})
				.then((accepted) => {
					if (!accepted) {
						return;
					}
					if (latestVersion.current !== version) {
						toast.error(t(`${keyPrefix}.versionMoved`, { name }));
						return;
					}
					action(version);
				})
				// A dismissed confirmation must not surface as an unhandled rejection: declining sends nothing.
				.catch(() => undefined);
		})();
	};

	return (
		<Group gap="xs" data-testid={testId ?? "external-app-instance-actions"}>
			<Button
				variant="filled"
				disabled={status !== "Running" || !openTarget?.url}
				aria-label={t(`${keyPrefix}.open`)}
				title={openTarget?.url ? undefined : t(`${keyPrefix}.openDisabled`)}
				onClick={() => window.open(openTarget?.url ?? "", "_blank", "noopener")}
				data-testid="external-app-action-open"
			>
				{t(`${keyPrefix}.open`)}
			</Button>

			{status === "Stopped" || status === "StoppedUnexpectedly" || status === "Failed" ? (
				<Button
					variant="default"
					loading={start.isPending}
					onClick={withVersion((version) =>
						start.mutate(
							{ path: { instanceId }, body: { expectedVersion: version } },
							{ onSuccess: announce("started"), onError },
						),
					)}
					data-testid="external-app-action-start"
				>
					{t(`${keyPrefix}.start`)}
				</Button>
			) : null}

			{/* Stop is offered wherever the server admits it — `AdmittedStatusFor` takes Running, Failed and
			    StoppedUnexpectedly, because a failed run leaves containers behind that only Stop tears down. */}
			{status === "Running" || status === "Failed" || status === "StoppedUnexpectedly" ? (
				<Button
					variant="default"
					loading={stop.isPending}
					onClick={withVersion((version) =>
						stop.mutate(
							{ path: { instanceId }, body: { expectedVersion: version } },
							{ onSuccess: announce("stopped"), onError },
						),
					)}
					data-testid="external-app-action-stop"
				>
					{t(`${keyPrefix}.stop`)}
				</Button>
			) : null}

			{/* Restart stays Running-only, and the installed table offers Open / Start / Stop / Details only: Restart is
			    a detail-page action. */}
			{status === "Running" && !compact ? (
				<Button
					variant="default"
					loading={restart.isPending}
					onClick={withVersion((version) =>
						restart.mutate(
							{ path: { instanceId }, body: { expectedVersion: version } },
							{ onSuccess: announce("restarted"), onError },
						),
					)}
					data-testid="external-app-action-restart"
				>
					{t(`${keyPrefix}.restart`)}
				</Button>
			) : null}

			{instance.updateAvailable === true && !busy && !compact ? (
				<Button variant="default" onClick={() => onUpdate?.()} data-testid="external-app-action-update">
					{t(`${keyPrefix}.update`)}
				</Button>
			) : null}

			{isExternalAppCancellable(status) && !compact ? (
				<Button
					variant="default"
					color="orange"
					loading={cancel.isPending}
					onClick={() => cancel.mutate({ path: { instanceId } }, { onSuccess: announce("cancelRequested"), onError })}
					data-testid="external-app-action-cancel"
				>
					{t(`${keyPrefix}.cancel`)}
				</Button>
			) : null}

			{compact ? null : (
				<>
					<Button
						variant="default"
						disabled={busy}
						loading={reset.isPending}
						onClick={() =>
							confirmThen("resetConfirm", (version) =>
								reset.mutate(
									{ path: { instanceId }, body: { expectedVersion: version } },
									{ onSuccess: announce("wasReset"), onError },
								),
							)
						}
						data-testid="external-app-action-reset"
					>
						{t(`${keyPrefix}.reset`)}
					</Button>

					<Button
						variant="default"
						color="red"
						disabled={busy}
						loading={uninstall.isPending}
						onClick={() =>
							confirmThen("uninstallConfirm", (version) =>
								// The token is a QUERY parameter here: uninstall has no body. The generated client types it optional;
								// the server does not, so it is always sent.
								uninstall.mutate(
									{ path: { instanceId }, query: { expectedVersion: version } },
									{ onSuccess: announce("uninstalled"), onError },
								),
							)
						}
						data-testid="external-app-action-uninstall"
					>
						{t(`${keyPrefix}.uninstall`)}
					</Button>
				</>
			)}
		</Group>
	);
}
