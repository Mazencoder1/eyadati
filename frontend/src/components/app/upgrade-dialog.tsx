"use client";

import { Sparkles } from "lucide-react";
import { useTranslations } from "next-intl";
import { Button, ButtonLink } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";

/**
 * Shown app-wide when the API answers 403 USAGE_LIMIT_REACHED / FEATURE_NOT_AVAILABLE. Which limit or feature is
 * read from the error details — nothing about plans is hard-coded here.
 */
export function UpgradeDialog() {
  const t = useTranslations("subscription");
  const tLimits = useTranslations("subscription.limits");
  const tFeatures = useTranslations("subscription.features");
  const { upgradeError, clearUpgradeError, can } = useAuth();
  const details = (upgradeError?.details ?? {}) as { limitKey?: string; max?: number; featureKey?: string };

  const subject = details.limitKey
    ? tLimits.has(details.limitKey as never)
      ? tLimits(details.limitKey as never)
      : details.limitKey
    : details.featureKey
      ? tFeatures.has(details.featureKey as never)
        ? tFeatures(details.featureKey as never)
        : details.featureKey
      : null;

  return (
    <Dialog
      open={!!upgradeError}
      onClose={clearUpgradeError}
      title={t("upgradeTitle")}
      size="sm"
      footer={
        <>
          <Button variant="outline" onClick={clearUpgradeError}>
            {t("notNow")}
          </Button>
          {can(P.Subscription.View) ? (
            <ButtonLink href="/subscription" onClick={clearUpgradeError} icon={<Sparkles className="size-4" aria-hidden="true" />}>
              {t("upgrade")}
            </ButtonLink>
          ) : null}
        </>
      }
    >
      <p className="text-sm text-fg">{upgradeError?.message}</p>
      {subject ? (
        <p className="mt-3 rounded-control bg-surface-muted px-3 py-2 text-sm text-fg-muted">
          {details.max !== undefined ? t("limitDetail", { subject, max: details.max }) : t("featureDetail", { subject })}
        </p>
      ) : null}
      {!can(P.Subscription.View) ? <p className="mt-3 text-sm text-fg-muted">{t("askOwner")}</p> : null}
    </Dialog>
  );
}
