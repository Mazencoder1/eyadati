/**
 * Accept-invitation flow (POST /auth/accept-invitation, T-010) as pure functions. The token travels only in the URL
 * (the link from the email, then the login page's internal `next` path) — never in storage.
 */
import { isApiError } from "@/lib/api/client";

export const ACCEPT_INVITATION_PATH = "/accept-invitation";

/** The accept page URL for a token (used as the login page's return path). */
export function invitationReturnPath(token: string): string {
  return `${ACCEPT_INVITATION_PATH}?token=${encodeURIComponent(token)}`;
}

/** Login URL that returns to the accept page and tells the login page why the user is there. */
export function invitationLoginHref(token: string): string {
  return `/login?notice=invitation&next=${encodeURIComponent(invitationReturnPath(token))}`;
}

export type AcceptOutcome =
  /** Existing account, wrong or no session: sign in as the invited account, then accept again. */
  | "sign-in-required"
  /** New account (no password yet): the password form is needed. */
  | "password-required"
  /** Used, expired or revoked link, or the seat is no longer pending. */
  | "invalid-link"
  /** Anything else (validation of the password, rate limit, network…): show the server's message. */
  | "error";

export function classifyAcceptError(error: unknown): AcceptOutcome {
  if (!isApiError(error)) return "error";
  if (error.status === 409 && error.code === "INVITATION_SIGN_IN_REQUIRED") return "sign-in-required";
  if (error.status === 400 && error.code === "INVALID_OR_EXPIRED_TOKEN") return "invalid-link";
  if (error.status === 400 && error.errors?.newPassword && Object.keys(error.errors).length === 1) return "password-required";
  return "error";
}
