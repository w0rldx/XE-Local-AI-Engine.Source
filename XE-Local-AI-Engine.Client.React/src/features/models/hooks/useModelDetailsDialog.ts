import { useDisclosure } from "@mantine/hooks";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useState } from "react";

import { getLocalModelDetailsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

/**
 * The per-model details dialog. Its details endpoint is fetched only while the dialog is open for a named model, so
 * the page never carries a persistent details read. `clear` is the delete path: the model it described is gone.
 */
export function useModelDetailsDialog(modelsAvailable: boolean) {
	// The model whose details dialog is open (also the only model whose details endpoint is fetched).
	const [modelName, setModelName] = useState<string | undefined>();
	const [opened, { open: openDialog, close }] = useDisclosure(false);

	const { data: details, isFetching } = useQuery({
		...withResponseValidation(getLocalModelDetailsOptions({ path: { modelName: modelName ?? "" } })),
		enabled: Boolean(opened && modelName && modelsAvailable),
	});

	const open = useCallback(
		(name: string) => {
			setModelName(name);
			openDialog();
		},
		[openDialog],
	);

	const clear = useCallback(() => {
		close();
		setModelName(undefined);
	}, [close]);

	return { modelName, opened, open, close, clear, details, isFetching };
}
