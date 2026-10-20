"use client";

import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/** Breadcrumb trail. Chevrons point "forward" in the reading direction (flipped in RTL). */
export function Breadcrumbs({ items }: { items: { label: ReactNode; href?: string }[] }) {
  const t = useTranslations("common");
  return (
    <nav aria-label={t("breadcrumbs")} className="min-w-0">
      <ol className="flex flex-wrap items-center gap-1 text-sm text-fg-muted">
        {items.map((item, i) => {
          const last = i === items.length - 1;
          return (
            <li key={i} className="flex min-w-0 items-center gap-1">
              {item.href && !last ? (
                <Link href={item.href} className="truncate rounded-sm hover:text-fg hover:underline">
                  {item.label}
                </Link>
              ) : (
                <span className={cn("truncate", last && "font-medium text-fg")} aria-current={last ? "page" : undefined}>
                  {item.label}
                </span>
              )}
              {!last ? <ChevronRight className="size-4 shrink-0 rtl:rotate-180" aria-hidden="true" /> : null}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** Builds a compact page list: 1 … 4 5 6 … 20 */
export function pageWindow(page: number, totalPages: number): (number | "gap")[] {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, i) => i + 1);
  const pages = new Set([1, totalPages, page - 1, page, page + 1].filter((p) => p >= 1 && p <= totalPages));
  const sorted = [...pages].sort((a, b) => a - b);
  const result: (number | "gap")[] = [];
  sorted.forEach((p, i) => {
    if (i > 0 && p - sorted[i - 1] > 1) result.push("gap");
    result.push(p);
  });
  return result;
}

export function Pagination({
  page,
  totalPages,
  totalCount,
  pageSize,
  onPageChange,
  disabled,
}: {
  page: number;
  totalPages: number;
  totalCount: number;
  pageSize: number;
  onPageChange: (page: number) => void;
  disabled?: boolean;
}) {
  const t = useTranslations("common.pagination");
  if (totalCount === 0) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);

  const navButton = (target: number, label: string, icon: ReactNode, isDisabled: boolean) => (
    <button
      type="button"
      onClick={() => onPageChange(target)}
      disabled={isDisabled || disabled}
      aria-label={label}
      className="flex size-9 items-center justify-center rounded-control text-fg-muted hover:bg-surface-muted disabled:opacity-40"
    >
      {icon}
    </button>
  );

  return (
    <nav
      aria-label={t("label")}
      className="flex flex-col items-center justify-between gap-3 border-t border-border px-4 py-3 sm:flex-row"
    >
      <p className="text-sm text-fg-muted">
        {t("summary", { from, to, total: totalCount })}
      </p>
      <ul className="flex flex-wrap items-center gap-1">
        <li>{navButton(1, t("first"), <ChevronsRight className="size-4 rtl:rotate-0 ltr:rotate-180" aria-hidden="true" />, page <= 1)}</li>
        <li>{navButton(page - 1, t("previous"), <ChevronRight className="size-4 ltr:rotate-180" aria-hidden="true" />, page <= 1)}</li>
        {pageWindow(page, totalPages).map((p, i) =>
          p === "gap" ? (
            <li key={`gap-${i}`} className="px-1 text-fg-subtle" aria-hidden="true">
              …
            </li>
          ) : (
            <li key={p}>
              <button
                type="button"
                onClick={() => onPageChange(p)}
                disabled={disabled}
                aria-current={p === page ? "page" : undefined}
                aria-label={t("page", { page: p })}
                className={cn(
                  "ltr-nums flex h-9 min-w-9 items-center justify-center rounded-control px-2 text-sm",
                  p === page ? "bg-primary text-primary-fg" : "text-fg hover:bg-surface-muted",
                )}
              >
                {p}
              </button>
            </li>
          ),
        )}
        <li>{navButton(page + 1, t("next"), <ChevronLeft className="size-4 ltr:rotate-180" aria-hidden="true" />, page >= totalPages)}</li>
        <li>{navButton(totalPages, t("last"), <ChevronsLeft className="size-4 ltr:rotate-180" aria-hidden="true" />, page >= totalPages)}</li>
      </ul>
    </nav>
  );
}
