import { create } from "zustand";

import type { NodeAccessTokenResponse, NodeAuthStoreState } from "@/core/auth/models/NodeAuthModels";

export const useNodeAuthStore = create<NodeAuthStoreState>()((set) => ({
	accessToken: undefined,
	expiresAtUtc: undefined,
	actions: {
		setToken: (token: NodeAccessTokenResponse) => {
			set({ accessToken: token.accessToken, expiresAtUtc: token.expiresAtUtc });
		},
		clear: () => {
			set({ accessToken: undefined, expiresAtUtc: undefined });
		},
	},
}));
