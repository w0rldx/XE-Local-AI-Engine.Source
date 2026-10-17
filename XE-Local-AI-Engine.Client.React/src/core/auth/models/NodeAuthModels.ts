import type {
	XeLocalAiEngineClientEndpointsAuthV1NodeAccessTokenResponse,
	XeLocalAiEngineClientEndpointsAuthV1NodeAuthStatusResponse,
	XeLocalAiEngineClientEndpointsAuthV1NodeChangePasswordRequest,
	XeLocalAiEngineClientEndpointsAuthV1NodeLoginRequest,
	XeLocalAiEngineClientEndpointsAuthV1NodeSetupRequest,
	XeLocalAiEngineClientEndpointsAuthV1NodeSetupResponse,
	XeLocalAiEngineClientEndpointsAuthV1NodeVaultConfirmRequest,
	XeLocalAiEngineClientEndpointsAuthV1NodeVaultConfirmResponse,
} from "@/core/api/generated/types.gen";

// "pending": the node key is not wrapped yet (fresh install before setup, or a legacy data dir awaiting /vault-setup).
// "locked": a minimal pre-host serves only the SPA, this status call and the unlock routes until the password is given.
export type NodeVaultState = "pending" | "locked" | "unlocked";

// The spec types `vault` as a plain string; the node only ever answers one of the three states.
export type NodeAuthStatusResponse = Omit<XeLocalAiEngineClientEndpointsAuthV1NodeAuthStatusResponse, "vault"> & {
	vault: NodeVaultState;
};

export type NodeSetupRequest = XeLocalAiEngineClientEndpointsAuthV1NodeSetupRequest;

// `recoveryCode` is null when the node runs on an operator-supplied secret and therefore has no vault to recover.
export type NodeSetupResponse = XeLocalAiEngineClientEndpointsAuthV1NodeSetupResponse;

// The vault confirm body; the pre-host's password unlock takes the same shape.
export type NodeVaultPasswordRequest = XeLocalAiEngineClientEndpointsAuthV1NodeVaultConfirmRequest;

export type NodeVaultConfirmResponse = XeLocalAiEngineClientEndpointsAuthV1NodeVaultConfirmResponse;

// The two unlock shapes stay hand-written: the locked pre-host answers them and they are not in the spec.
// The recovery unlock rotates the code: the one just used stops working, and this one is shown exactly once.
export interface NodeVaultRecoveryUnlockResponse {
	recoveryCode: string;
}

export interface NodeVaultRecoveryUnlockRequest {
	recoveryCode: string;
	newPassword: string;
}

export type NodeLoginRequest = XeLocalAiEngineClientEndpointsAuthV1NodeLoginRequest;

export type NodeChangePasswordRequest = XeLocalAiEngineClientEndpointsAuthV1NodeChangePasswordRequest;

export type NodeAccessTokenResponse = XeLocalAiEngineClientEndpointsAuthV1NodeAccessTokenResponse;

export interface NodeAuthErrorResponse {
	message: string;
	errors?: string[];
}

export interface NodeAuthStoreState {
	accessToken?: string;
	expiresAtUtc?: string;
	actions: {
		setToken: (token: NodeAccessTokenResponse) => void;
		clear: () => void;
	};
}
