"use client";

import { useEffect, useState, useSyncExternalStore } from "react";

/**
 * One-time links (password reset, email verification, invitation) carry their token in the URL FRAGMENT:
 * `/reset-password#token=…`. A fragment is never sent in an HTTP request, so the token cannot end up in a reverse proxy's
 * or load balancer's access log, in the web server's log, or in a Referer header. Links emailed before this change used
 * the query string (`?token=…`); they stay valid until they expire, so the query form is still read.
 */
export function readOneTimeToken(location: { hash: string; search: string }): string {
  const fromHash = new URLSearchParams(location.hash.replace(/^#/, "")).get("token");
  if (fromHash) return fromHash;
  return new URLSearchParams(location.search).get("token") ?? "";
}

const subscribeNothing = () => () => {};

/**
 * Reads the token once and removes it from the address bar (and so from the browser history entry and any later "copy
 * link"), keeping it only in memory. `ready` is false while the page is server-rendered/hydrating and true on the client,
 * so the server never has to guess whether a token exists.
 */
export function useOneTimeToken(): { token: string; ready: boolean } {
  const ready = useSyncExternalStore(subscribeNothing, () => true, () => false);
  const [token] = useState(() => (typeof window === "undefined" ? "" : readOneTimeToken(window.location)));

  useEffect(() => {
    if (token) window.history.replaceState(window.history.state, "", window.location.pathname);
  }, [token]);

  return { token, ready };
}
