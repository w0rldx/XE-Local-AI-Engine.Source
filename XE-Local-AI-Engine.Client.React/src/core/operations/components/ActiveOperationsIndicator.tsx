import { Loader } from "@mantine/core";
import { IconAlertTriangle, IconHandStop } from "@tabler/icons-react";
import { useRouterState } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { useActiveOperations } from "@/core/operations/hooks/useActiveOperations";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";

// The compact app-wide signal for running and failed operations, in the header on desktop and mobile alike. It links
// to Home, whose Activity card lists the operations with their detail and actions. On Home itself it hides: the card
// shows the same rows right there, and a link to the page already open would do nothing. A failure keeps it in the
// error state until the source leaves its failed state, so a first-run runtime failure is visible on every page. A run
// waiting for the user comes next: it needs a decision, but nothing has gone wrong.
export function ActiveOperationsIndicator() {
	const { t } = useTranslation();
	const operations = useActiveOperations();
	const onHome = useRouterState({ select: (state) => state.location.pathname === "/" });

	if (operations.length === 0 || onHome) {
		return null;
	}

	const failed = operations.filter((operation) => operation.state === "failed").length;
	const waiting = operations.filter((operation) => operation.state === "waiting").length;
	const running = operations.length - failed - waiting;
	const state = failed > 0 ? "failed" : waiting > 0 ? "waiting" : "running";
	// The only progress worth a number in a chip is a single operation's own reported percentage.
	const single = operations.length === 1 ? operations[0] : undefined;
	const pct = single?.state === "running" && single.progress.kind === "known" ? single.progress.pct : undefined;

	return (
		<ButtonLink
			to="/"
			size="compact-sm"
			variant="light"
			color={state === "failed" ? "red" : state === "waiting" ? "yellow" : "primary"}
			leftSection={
				state === "failed" ? (
					<IconAlertTriangle size={14} />
				) : state === "waiting" ? (
					<IconHandStop size={14} />
				) : (
					<Loader size={12} />
				)
			}
			data-testid="active-operations-indicator"
			data-state={state}
		>
			{state === "failed"
				? t("operations.indicator.actionRequired", "Action required")
				: state === "waiting"
					? t("operations.indicator.waiting", "{{count}} waiting for you", { count: waiting })
					: pct !== undefined
						? t("operations.indicator.runningPercent", "1 running · {{pct}}%", { pct })
						: t("operations.indicator.running", "{{count}} running", { count: running })}
		</ButtonLink>
	);
}
