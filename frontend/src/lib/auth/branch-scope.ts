/**
 * Branch scope of the signed-in member (GET /auth/me → branchIds; empty = every branch). UI gating only: the API
 * rejects out-of-scope writes with 403 BRANCH_ACCESS_DENIED and hides out-of-scope doctors (404).
 */
import type { Branch, Guid } from "@/lib/api/types";

export function isBranchRestricted(scope: readonly Guid[]): boolean {
  return scope.length > 0;
}

/** May this member edit the branch itself (Branches.Manage is checked separately)? */
export function canEditBranch(scope: readonly Guid[], branchId: Guid): boolean {
  return !isBranchRestricted(scope) || scope.includes(branchId);
}

/** Restricted members cannot create branches (a new branch would be outside their scope). */
export function canCreateBranch(scope: readonly Guid[]): boolean {
  return !isBranchRestricted(scope);
}

/** Active branches a member may assign working hours or staff to. */
export function assignableBranches(branches: readonly Branch[], scope: readonly Guid[]): Branch[] {
  return branches.filter((b) => b.isActive && canEditBranch(scope, b.id));
}
