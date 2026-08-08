import { use } from "react";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";

export function useConfirm() {
	const context = use(ConfirmContext);

	if (!context) {
		throw new Error("useConfirm must be used within a ConfirmProvider");
	}

	return context;
}
