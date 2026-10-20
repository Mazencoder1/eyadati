import type { ReactNode } from "react";
import { HOME_PATH } from "@/lib/routes";
import { cn } from "@/lib/utils";

export function AuthCard({
  title,
  description,
  children,
  footer,
  wide = false,
}: {
  title: ReactNode;
  description?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}) {
  return (
    <div className={cn("w-full", wide ? "max-w-2xl" : "max-w-md")}>
      <div className="rounded-dialog border border-border bg-surface p-6 shadow-raised sm:p-8">
        <h1 className="text-xl font-bold text-fg">{title}</h1>
        {description ? <p className="mt-1 text-sm text-fg-muted">{description}</p> : null}
        <div className="mt-6">{children}</div>
      </div>
      {footer ? <div className="mt-4 text-center text-sm text-fg-muted">{footer}</div> : null}
    </div>
  );
}

/** ASCII control characters (the URL parser silently drops tab/LF/CR) and backslashes (read as "/"). */
const UNSAFE_PATH_CHARS = /[\u0000-\u001f\u007f\\]/;
const PROBE_ORIGIN = "https://app.invalid";

/**
 * Only same-app relative paths are accepted as a post-login destination (no open redirects, QA-17).
 * Rejected (→ home): anything not starting with a single "/", protocol-relative "//" or "/\\", control characters
 * and backslashes anywhere (the browser strips or rewrites them, e.g. "/\t/evil" becomes "//evil"), and the same
 * patterns hidden behind percent-encoding. Finally the value must resolve to the app's own origin.
 */
export function safeNextPath(next: string | null | undefined): string {
  if (typeof next !== "string" || !next.startsWith("/") || next.startsWith("//") || UNSAFE_PATH_CHARS.test(next)) return HOME_PATH;

  // Percent-encoded variants (%2F%2F, %5C, %09, double encoding…): decode until stable and check every layer.
  let decoded = next;
  for (let i = 0; i < 3; i++) {
    let layer: string;
    try {
      layer = decodeURIComponent(decoded);
    } catch {
      return HOME_PATH;
    }
    if (layer === decoded) break;
    if (layer.startsWith("//") || UNSAFE_PATH_CHARS.test(layer)) return HOME_PATH;
    decoded = layer;
  }
  if (decoded !== next && /%[0-9a-f]{2}/i.test(decoded)) return HOME_PATH; // still encoded after 3 layers

  try {
    return new URL(next, PROBE_ORIGIN).origin === PROBE_ORIGIN ? next : HOME_PATH;
  } catch {
    return HOME_PATH;
  }
}
