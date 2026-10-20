"use client";

import { useCallback, useEffect, useRef, useState } from "react";

interface QueryState<T> {
  key: string | null;
  data?: T;
  error?: unknown;
}

/**
 * Minimal data-fetching hook: refetches when `key` changes, aborts stale requests, keeps the previous data while
 * a new page loads (no table flicker). Pass `key = null` to skip fetching.
 */
export function useQuery<T>(key: string | null, fetcher: (signal: AbortSignal) => Promise<T>) {
  const [state, setState] = useState<QueryState<T>>({ key: null });
  const [version, setVersion] = useState(0);
  const fetcherRef = useRef(fetcher);

  useEffect(() => {
    fetcherRef.current = fetcher;
  });

  const fullKey = key === null ? null : `${key}#${version}`;

  useEffect(() => {
    if (fullKey === null) return;
    const controller = new AbortController();
    fetcherRef.current(controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) setState({ key: fullKey, data });
      },
      (error: unknown) => {
        if (!controller.signal.aborted) setState((s) => ({ key: fullKey, data: s.data, error }));
      },
    );
    return () => controller.abort();
  }, [fullKey]);

  const loading = fullKey !== null && state.key !== fullKey;
  const reload = useCallback(() => setVersion((v) => v + 1), []);
  /** Local update after a mutation (e.g. replace one row) without refetching. */
  const setData = useCallback((update: (current: T | undefined) => T | undefined) => {
    setState((s) => ({ ...s, data: update(s.data) }));
  }, []);

  return {
    data: state.data,
    error: loading ? undefined : state.error,
    loading,
    /** First load only (no data yet) — show skeletons; later loads keep the old rows visible. */
    initialLoading: loading && state.data === undefined,
    reload,
    setData,
  };
}

/** Returns `value` after it has stopped changing for `delayMs` (used for search inputs). */
export function useDebouncedValue<T>(value: T, delayMs = 350): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(id);
  }, [value, delayMs]);
  return debounced;
}

/** Current time, refreshed every `intervalMs` (keeps time-based UI such as "no-show" availability current). */
export function useNow(intervalMs = 60_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}
