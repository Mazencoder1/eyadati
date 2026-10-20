"use client";

import { Check, CreditCard, Minus } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge, type Tone } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { LoadingBlock } from "@/components/ui/spinner";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { PublicPlan, SubscriptionView, UsageItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { formatDateTime } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { cn } from "@/lib/utils";

const statusTone: Record<SubscriptionView["status"], Tone> = {
  Trialing: "info",
  Active: "success",
  PastDue: "warning",
  Canceled: "neutral",
  Expired: "danger",
};

export function SubscriptionPage() {
  return (
    <RequirePermission anyOf={[P.Subscription.View]}>
      <Subscription />
    </RequirePermission>
  );
}

function Subscription() {
  const t = useTranslations("subscription");
  const tLimits = useTranslations("subscription.limits");
  const tFeatures = useTranslations("subscription.features");
  const locale = useLocale();
  const { api } = useAuth();
  const { timeZone } = useClinic();
  const sub = useQuery("subscription", () => api.subscription.current());
  const plans = useQuery("plans", () => api.subscription.plans());
  const [upgradeTarget, setUpgradeTarget] = useState<PublicPlan | null>(null);

  const limitLabel = (key: string) => (tLimits.has(key as never) ? tLimits(key as never) : key);
  const featureLabel = (key: string) => (tFeatures.has(key as never) ? tFeatures(key as never) : key);

  if (sub.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (sub.error && sub.data === undefined) {
    return <Alert tone="danger">{isApiError(sub.error) ? sub.error.message : t("loadFailed")}</Alert>;
  }
  const s = sub.data;
  const planName = (p: { nameAr: string; nameEn: string }) => (locale === "ar" ? p.nameAr : p.nameEn);
  const currentIndex = (plans.data ?? []).findIndex((p) => p.code === s?.planCode);

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} />
      {!s ? (
        <EmptyState icon={<CreditCard className="size-6" />} title={t("noSubscription")} description={t("noSubscriptionHint")} />
      ) : (
        <div className="flex flex-col gap-6">
          {!s.hasFullAccess ? (
            <Alert tone="danger" title={t("expiredTitle")}>
              {t("expiredBody")}
            </Alert>
          ) : null}
          <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
            <Card className="xl:col-span-1">
              <CardHeader title={t("currentPlan")} />
              <CardBody className="flex flex-col gap-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-2xl font-bold">{locale === "ar" ? s.planNameAr : s.planNameEn}</span>
                  {/* The period end decides access: a stored "Active" whose period ended is shown as ended. */}
                  {s.hasFullAccess ? (
                    <Badge tone={statusTone[s.status]}>{t(`statuses.${s.status}`)}</Badge>
                  ) : (
                    <Badge tone={statusTone.Expired}>{t("statuses.Expired")}</Badge>
                  )}
                </div>
                {/* One end date (GD-13): during a trial the trial end is the period end, so it is not repeated. */}
                <p className="text-sm text-fg-muted">
                  {t(`cycles.${s.billingCycle}`)} ·{" "}
                  {s.status === "Trialing" && s.trialEndsAtUtc && s.hasFullAccess ? (
                    <>
                      {t("trialEnds")} <span className="ltr-nums">{formatDateTime(s.trialEndsAtUtc, timeZone, locale)}</span>
                    </>
                  ) : (
                    <>
                      {t("periodEnds")} <span className="ltr-nums">{formatDateTime(s.currentPeriodEndUtc, timeZone, locale)}</span>
                    </>
                  )}
                </p>
                {s.cancelAtPeriodEnd ? <Alert tone="warning">{t("cancelAtPeriodEnd")}</Alert> : null}
              </CardBody>
            </Card>

            <Card className="xl:col-span-2">
              <CardHeader title={t("usage")} description={t("usageHint")} />
              <CardBody>
                <ul className="grid grid-cols-1 gap-5 md:grid-cols-2">
                  {s.usage.map((u) => (
                    <UsageBar key={u.limitKey} item={u} label={limitLabel(u.limitKey)} />
                  ))}
                </ul>
              </CardBody>
            </Card>
          </div>

          {Object.keys(s.features).length > 0 ? (
            <Card>
              <CardHeader title={t("featuresTitle")} />
              <CardBody>
                <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
                  {Object.entries(s.features).map(([key, enabled]) => (
                    <li key={key} className="flex items-center gap-2 text-sm">
                      {enabled ? (
                        <Check className="size-4 shrink-0 text-success-strong" aria-hidden="true" />
                      ) : (
                        <Minus className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
                      )}
                      <span className={cn(!enabled && "text-fg-subtle")}>{featureLabel(key)}</span>
                      <span className="sr-only">{enabled ? t("included") : t("notIncluded")}</span>
                    </li>
                  ))}
                </ul>
              </CardBody>
            </Card>
          ) : null}

          {plans.data && plans.data.length > 0 ? (
            <section aria-labelledby="plans-heading">
              <h2 id="plans-heading" className="mb-3 text-lg font-semibold">
                {t("plansTitle")}
              </h2>
              <ul className="grid grid-cols-1 gap-4 md:grid-cols-2 2xl:grid-cols-4">
                {plans.data.map((p, i) => {
                  const current = p.code === s.planCode;
                  return (
                    <li key={p.code}>
                      <Card className={cn("flex h-full flex-col", current && "border-primary ring-1 ring-primary")}>
                        <CardBody className="flex flex-1 flex-col gap-3">
                          <div className="flex items-center justify-between gap-2">
                            <h3 className="text-base font-semibold">{planName(p)}</h3>
                            {current ? <Badge tone="primary">{t("current")}</Badge> : null}
                          </div>
                          {/* GD-10: the stored plan prices are placeholders, so none is shown until real prices are set. */}
                          <p className="text-sm text-fg-muted">{t("pricesLater")}</p>
                          <ul className="flex flex-1 flex-col gap-1 text-sm text-fg-muted">
                            {["max_users", "max_doctors", "max_patients", "max_branches"].map((key) => (
                              <li key={key}>
                                {limitLabel(key)}:{" "}
                                <span className="ltr-nums font-medium text-fg">
                                  {p.limits[key] === null || p.limits[key] === undefined ? t("unlimited") : p.limits[key]}
                                </span>
                              </li>
                            ))}
                          </ul>
                          {!current && i > currentIndex ? (
                            <Button variant="outline" onClick={() => setUpgradeTarget(p)}>
                              {t("upgrade")}
                            </Button>
                          ) : null}
                        </CardBody>
                      </Card>
                    </li>
                  );
                })}
              </ul>
            </section>
          ) : null}
        </div>
      )}

      <Dialog
        open={!!upgradeTarget}
        onClose={() => setUpgradeTarget(null)}
        title={t("upgradeTitle")}
        size="sm"
        footer={<Button onClick={() => setUpgradeTarget(null)}>{t("ok")}</Button>}
      >
        <p className="text-sm">{upgradeTarget ? t("upgradeContact", { plan: planName(upgradeTarget) }) : null}</p>
      </Dialog>
    </>
  );
}

function UsageBar({ item, label }: { item: UsageItem; label: string }) {
  const t = useTranslations("subscription");
  const unlimited = item.max === null || item.max === undefined;
  const max = item.max ?? 0;
  const ratio = unlimited ? 0 : max === 0 ? 1 : Math.min(1, item.used / max);
  const tone = ratio >= 1 ? "bg-danger" : ratio >= 0.8 ? "bg-warning-strong" : "bg-primary";
  return (
    <li className="flex flex-col gap-1.5">
      <div className="flex items-center justify-between gap-2 text-sm">
        <span className="font-medium">{label}</span>
        <span className="ltr-nums text-fg-muted">{unlimited ? `${item.used} / ∞` : `${item.used} / ${max}`}</span>
      </div>
      {unlimited ? (
        <p className="text-xs text-fg-subtle">{t("unlimited")}</p>
      ) : (
        <div
          role="progressbar"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={max}
          aria-valuenow={Math.min(item.used, max)}
          aria-valuetext={t("usageValue", { used: item.used, max })}
          className="h-2 overflow-hidden rounded-pill bg-surface-muted"
        >
          <div className={cn("h-full rounded-pill", tone)} style={{ width: `${Math.round(ratio * 100)}%` }} />
        </div>
      )}
      {!unlimited && ratio >= 1 ? <p className="text-xs font-medium text-danger-strong">{t("limitReached")}</p> : null}
    </li>
  );
}
