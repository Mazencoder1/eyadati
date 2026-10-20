"use client";

import { ArrowDown, ArrowUp, ArrowUpDown, Columns3, Search } from "lucide-react";
import { useTranslations } from "next-intl";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import type { PageMeta } from "@/lib/api/types";
import { useDebouncedValue } from "@/lib/hooks/use-query";
import { cn } from "@/lib/utils";
import { Alert } from "./alert";
import { Button } from "./button";
import { EmptyState } from "./empty-state";
import { Pagination } from "./navigation";
import { Skeleton } from "./spinner";

export interface Column<T> {
  id: string;
  header: string;
  cell: (row: T) => ReactNode;
  /** Server sort key (e.g. "fullName"); omit for unsortable columns. */
  sortKey?: string;
  /** Can the user hide it from the column menu? */
  hideable?: boolean;
  /** Hidden until the user turns it on. */
  defaultHidden?: boolean;
  className?: string;
}

export interface SortState {
  key: string;
  desc: boolean;
}

/** "-createdAt" ⇄ { key: "createdAt", desc: true } */
export function parseSort(sort: string | undefined): SortState | undefined {
  if (!sort) return undefined;
  return sort.startsWith("-") ? { key: sort.slice(1), desc: true } : { key: sort, desc: false };
}
export function formatSort(sort: SortState | undefined): string | undefined {
  if (!sort) return undefined;
  return sort.desc ? `-${sort.key}` : sort.key;
}

/**
 * Server-driven table: the parent owns page/sort/search state and fetches; this renders loading, empty and error
 * states, sortable headers (aria-sort), a column visibility menu, a debounced search box and pagination.
 * Below the `md` breakpoint rows reflow into labelled cards, so the page never scrolls horizontally.
 */
export function DataTable<T>({
  caption,
  columns,
  rows,
  rowKey,
  loading,
  error,
  onRetry,
  meta,
  onPageChange,
  sort,
  onSortChange,
  search,
  onSearchChange,
  searchPlaceholder,
  toolbar,
  empty,
  rowActions,
}: {
  caption: string;
  columns: Column<T>[];
  rows: T[] | undefined;
  rowKey: (row: T) => string;
  loading?: boolean;
  error?: string | null;
  onRetry?: () => void;
  meta?: PageMeta;
  onPageChange?: (page: number) => void;
  sort?: SortState;
  onSortChange?: (sort: SortState | undefined) => void;
  search?: string;
  /** Called with the debounced value. */
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  toolbar?: ReactNode;
  empty?: ReactNode;
  rowActions?: (row: T) => ReactNode;
}) {
  const t = useTranslations("common.table");
  const [hidden, setHidden] = useState<Set<string>>(
    () => new Set(columns.filter((c) => c.defaultHidden).map((c) => c.id)),
  );
  const visible = columns.filter((c) => !hidden.has(c.id));
  const hideable = columns.filter((c) => c.hideable);
  const showSkeleton = loading && rows === undefined;

  const toggleSort = (key: string) => {
    if (!onSortChange) return;
    if (sort?.key !== key) onSortChange({ key, desc: false });
    else if (!sort.desc) onSortChange({ key, desc: true });
    else onSortChange(undefined);
  };

  return (
    <div className="min-w-0">
      {onSearchChange || toolbar || hideable.length > 0 ? (
        <div className="flex flex-col gap-3 px-4 py-3 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          {onSearchChange ? (
            <SearchBox initial={search ?? ""} onChange={onSearchChange} placeholder={searchPlaceholder ?? t("search")} />
          ) : (
            <span />
          )}
          <div className="flex flex-wrap items-center gap-2">
            {toolbar}
            {hideable.length > 0 ? (
              <ColumnMenu
                columns={hideable}
                hidden={hidden}
                onToggle={(id) =>
                  setHidden((prev) => {
                    const next = new Set(prev);
                    if (next.has(id)) next.delete(id);
                    else next.add(id);
                    return next;
                  })
                }
              />
            ) : null}
          </div>
        </div>
      ) : null}

      {error ? (
        <div className="px-4 pb-4 sm:px-6">
          <Alert
            tone="danger"
            title={t("loadFailed")}
            action={
              onRetry ? (
                <Button size="sm" variant="outline" onClick={onRetry}>
                  {t("retry")}
                </Button>
              ) : undefined
            }
          >
            {error}
          </Alert>
        </div>
      ) : null}

      <div className="relative" aria-busy={loading || undefined}>
        <table className="w-full border-collapse text-sm">
          <caption className="sr-only">{caption}</caption>
          <thead className="hidden border-y border-border bg-surface-muted text-start md:table-header-group">
            <tr>
              {visible.map((col) => {
                const active = sort?.key === col.sortKey;
                const ariaSort = col.sortKey ? (active ? (sort!.desc ? "descending" : "ascending") : "none") : undefined;
                return (
                  <th
                    key={col.id}
                    scope="col"
                    aria-sort={ariaSort}
                    className={cn("px-4 py-2.5 text-start text-xs font-semibold text-fg-muted first:ps-6 last:pe-6", col.className)}
                  >
                    {col.sortKey && onSortChange ? (
                      <button
                        type="button"
                        onClick={() => toggleSort(col.sortKey!)}
                        className="inline-flex items-center gap-1 rounded-sm hover:text-fg"
                      >
                        {col.header}
                        {active ? (
                          sort!.desc ? (
                            <ArrowDown className="size-3.5" aria-hidden="true" />
                          ) : (
                            <ArrowUp className="size-3.5" aria-hidden="true" />
                          )
                        ) : (
                          <ArrowUpDown className="size-3.5 opacity-50" aria-hidden="true" />
                        )}
                      </button>
                    ) : (
                      col.header
                    )}
                  </th>
                );
              })}
              {rowActions ? (
                <th scope="col" className="px-4 py-2.5 pe-6 text-end text-xs font-semibold text-fg-muted">
                  <span className="sr-only">{t("actions")}</span>
                </th>
              ) : null}
            </tr>
          </thead>
          <tbody className={cn(loading && rows !== undefined && "opacity-60 transition-opacity")}>
            {showSkeleton
              ? Array.from({ length: 5 }, (_, i) => (
                  <tr key={i} className="border-b border-border">
                    <td colSpan={visible.length + (rowActions ? 1 : 0)} className="px-4 py-3 sm:px-6">
                      <Skeleton className="h-5 w-full" />
                    </td>
                  </tr>
                ))
              : rows?.map((row) => (
                  <tr
                    key={rowKey(row)}
                    className="block border-b border-border px-4 py-3 md:table-row md:px-0 md:py-0 md:hover:bg-surface-muted/60"
                  >
                    {visible.map((col) => (
                      <td
                        key={col.id}
                        data-label={col.header}
                        className={cn(
                          "flex min-w-0 items-start justify-between gap-3 py-1 text-fg md:table-cell md:px-4 md:py-3 md:first:ps-6 md:last:pe-6",
                          "before:shrink-0 before:text-xs before:font-medium before:text-fg-subtle before:content-[attr(data-label)] md:before:content-none",
                          col.className,
                        )}
                      >
                        <div className="min-w-0 text-end break-words md:text-start">{col.cell(row)}</div>
                      </td>
                    ))}
                    {rowActions ? (
                      <td className="flex justify-end gap-2 pt-2 md:table-cell md:px-4 md:py-3 md:pe-6 md:text-end">
                        <div className="inline-flex flex-wrap justify-end gap-2">{rowActions(row)}</div>
                      </td>
                    ) : null}
                  </tr>
                ))}
          </tbody>
        </table>
        {!showSkeleton && !error && rows?.length === 0 ? (empty ?? <EmptyState title={t("empty")} />) : null}
      </div>

      {meta && onPageChange ? (
        <Pagination
          page={meta.page}
          totalPages={meta.totalPages}
          totalCount={meta.totalCount}
          pageSize={meta.pageSize}
          onPageChange={onPageChange}
          disabled={loading}
        />
      ) : null}
    </div>
  );
}

/** Debounced search input (350 ms) so typing doesn't fire a request per keystroke. */
export function SearchBox({
  initial,
  onChange,
  placeholder,
  className,
}: {
  initial: string;
  onChange: (value: string) => void;
  placeholder: string;
  className?: string;
}) {
  const id = useId();
  const [value, setValue] = useState(initial);
  const debounced = useDebouncedValue(value.trim(), 350);
  const onChangeRef = useRef(onChange);
  const lastSent = useRef(initial.trim());

  useEffect(() => {
    onChangeRef.current = onChange;
  });

  useEffect(() => {
    if (debounced === lastSent.current) return;
    lastSent.current = debounced;
    onChangeRef.current(debounced);
  }, [debounced]);

  return (
    <div className={cn("relative w-full sm:max-w-xs", className)}>
      <label htmlFor={id} className="sr-only">
        {placeholder}
      </label>
      <Search
        className="pointer-events-none absolute start-3 top-1/2 size-4 -translate-y-1/2 text-fg-subtle"
        aria-hidden="true"
      />
      <input
        id={id}
        type="search"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        placeholder={placeholder}
        className="h-10 w-full rounded-control border border-border-input bg-surface ps-9 pe-3 text-sm placeholder:text-fg-subtle focus-visible:outline-2 focus-visible:outline-focus"
      />
    </div>
  );
}

function ColumnMenu<T>({
  columns,
  hidden,
  onToggle,
}: {
  columns: Column<T>[];
  hidden: Set<string>;
  onToggle: (id: string) => void;
}) {
  const t = useTranslations("common.table");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const menuId = useId();

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDoc);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div ref={ref} className="relative">
      <Button
        variant="outline"
        size="sm"
        aria-expanded={open}
        aria-controls={menuId}
        onClick={() => setOpen((o) => !o)}
        icon={<Columns3 className="size-4" aria-hidden="true" />}
      >
        {t("columns")}
      </Button>
      {open ? (
        <div
          id={menuId}
          className="absolute end-0 z-20 mt-1 w-56 rounded-card border border-border bg-surface p-2 shadow-floating"
        >
          <p className="px-2 pb-1 text-xs font-semibold text-fg-subtle">{t("showColumns")}</p>
          {columns.map((c) => (
            <label key={c.id} className="flex cursor-pointer items-center gap-2 rounded-control px-2 py-1.5 text-sm hover:bg-surface-muted">
              <input
                type="checkbox"
                checked={!hidden.has(c.id)}
                onChange={() => onToggle(c.id)}
                className="size-4 accent-[var(--c-primary)]"
              />
              {c.header}
            </label>
          ))}
        </div>
      ) : null}
    </div>
  );
}
