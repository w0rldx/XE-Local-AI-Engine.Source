import { Loader } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useRouterState } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { useActiveOperations } from "@/core/operations/hooks/useActiveOperations";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";

// The compact app-wide signal for running and failed operations, in the header on desktop and mobile alike. It links
// to Home, whose Activity card lists the operations with their detail and actions. On Home itself it hides: the card
// shows the same rows right there, and a link to the page already open would do nothing. A failure keeps it in the
// error state until the source leaves its failed state, so a first-run runtime failure is visible on every page.
export function ActiveOperationsIndicator() {
	const { t } = useTranslation();
	const operations = useActiveOperations();
	const onHome = useRouterState({ select: (state) => state.location.pathname === "/" });

	if (operations.length === 0 || onHome) {
		return null;
	}

	const failed = operations.filter((operation) => operation.state === "failed").length;
	const running = operations.length - failed;
	// The only progress worth a number in a chip is a single operation's own reported percentage.
	const single = operations.length === 1 ? operations[0] : undefined;
	const pct = single?.state === "running" && single.progress.kind === "known" ? single.progress.pct : undefined;

	return (
		<ButtonLink
			to="/"
			size="compact-sm"
			variant="light"
			color={failed > 0 ? "red" : "primary"}
			leftSection={failed > 0 ? <IconAlertTriangle size={14} /> : <Loader size={12} />}
			data-testid="active-operations-indicator"
			data-state={failed > 0 ? "failed" : "running"}
		>
			{failed > 0
				? t("operations.indicator.actionRequired", "Action required")
				: pct !== undefined
					? t("operations.indicator.runningPercent", "1 running · {{pct}}%", { pct })
					: t("operations.indicator.running", "{{count}} running", { count: running })}
		</ButtonLink>
	);
}
