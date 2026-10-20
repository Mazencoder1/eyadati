"use client";

import { ShieldOff } from "lucide-react";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { EmptyState } from "@/components/ui/empty-state";
import { Breadcrumbs } from "@/components/ui/navigation";
import { useAuth } from "@/lib/auth/session";

export function PageHeader({
  title,
  description,
  actions,
  breadcrumbs,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  breadcrumbs?: { label: ReactNode; href?: string }[];
}) {
  return (
    <div className="mb-6 flex flex-col gap-3">
      {breadcrumbs ? <Breadcrumbs items={breadcrumbs} /> : null}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div className="min-w-0">
          <h1 className="text-2xl font-bold text-fg">{title}</h1>
          {description ? <p className="mt-1 text-sm text-fg-muted">{description}</p> : null}
        </div>
        {actions ? <div className="flex flex-wrap items-center gap-2">{actions}</div> : null}
      </div>
    </div>
  );
}

/** Page-level guard for direct URL access. UX only — the API rejects the calls anyway. */
export function RequirePermission({ anyOf, children }: { anyOf: string[]; children: ReactNode }) {
  const t = useTranslations("shell");
  const { canAny } = useAuth();
  if (!canAny(...anyOf)) {
    return (
      <EmptyState
        icon={<ShieldOff className="size-6" />}
        title={t("forbiddenTitle")}
        description={t("forbiddenDescription")}
        className="py-24"
      />
    );
  }
  return <>{children}</>;
}
