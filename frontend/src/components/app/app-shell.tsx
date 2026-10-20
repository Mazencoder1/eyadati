"use client";

import { Building2, LogOut, Menu, ShieldCheck, Stethoscope, UserRound, X } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { LoadingBlock } from "@/components/ui/spinner";
import { useToast } from "@/components/ui/toast";
import { ClinicProvider, useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import { useAuth } from "@/lib/auth/session";
import { useQuery } from "@/lib/hooks/use-query";
import { HOME_PATH } from "@/lib/routes";
import { cn } from "@/lib/utils";
import { LocaleSwitcher } from "./locale-switcher";
import { visibleNavItems } from "./nav-items";
import { UpgradeDialog } from "./upgrade-dialog";

/**
 * Authenticated layout: session guard → clinic context → sidebar/topbar. Navigation only lists sections the
 * user has permissions for; each page still guards itself and the API enforces every call.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const t = useTranslations("shell");
  const { status, session, logout, signedOut } = useAuth();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (status === "anonymous") router.replace(signedOut ? "/login" : `/login?next=${encodeURIComponent(pathname)}`);
  }, [status, signedOut, pathname, router]);

  if (status !== "authenticated" || !session) {
    return <LoadingBlock label={t("loadingSession")} className="min-h-dvh" />;
  }

  if (!session.tenant) return <ChooseClinic onLogout={() => void logout()} />;

  return (
    <ClinicProvider key={session.tenant.id} tenantId={session.tenant.id}>
      <ClinicAccessGate>
        <ShellLayout>{children}</ShellLayout>
      </ClinicAccessGate>
    </ClinicProvider>
  );
}

/**
 * Session without a clinic: a platform account (login without tenantId, T-008) or a user whose clinic access ended.
 * Offers the clinics the account is an active member of (/auth/tenants → switch-tenant); otherwise explains why
 * there is nothing to open. The platform console is not part of this app.
 */
export function ChooseClinic({ onLogout }: { onLogout: () => void }) {
  const t = useTranslations("shell");
  const { session, api } = useAuth();
  const [switching, setSwitching] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const tenants = useQuery(session ? `tenants:${session.user.id}` : null, () => api.auth.tenants());
  const isPlatform = !!session && session.user.platformRole !== "None";
  const clinics = tenants.data ?? [];
  const logoutButton = (
    <Button variant="outline" onClick={onLogout} icon={<LogOut className="size-4 rtl:rotate-180" aria-hidden="true" />}>
      {t("logout")}
    </Button>
  );
  // A platform account has no clinic of its own; this is where it manages its two-step verification.
  const securityLink = isPlatform ? (
    <ButtonLink href="/security" variant="outline" icon={<ShieldCheck className="size-4" aria-hidden="true" />}>
      {t("twoStepLink")}
    </ButtonLink>
  ) : null;

  if (tenants.initialLoading) return <LoadingBlock label={t("loadingSession")} className="min-h-dvh" />;

  if (clinics.length === 0) {
    return (
      <main className="flex min-h-dvh items-center justify-center p-4">
        <EmptyState
          icon={<Building2 className="size-6" />}
          title={isPlatform ? t("platformTitle") : t("noClinicTitle")}
          description={isPlatform ? t("platformNoClinics") : t("noClinicDescription")}
          action={
            <>
              {securityLink}
              {logoutButton}
            </>
          }
        />
      </main>
    );
  }

  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <div className="w-full max-w-md rounded-dialog border border-border bg-surface p-6 shadow-raised sm:p-8">
        <h1 className="text-xl font-bold text-fg">{isPlatform ? t("platformTitle") : t("chooseClinicTitle")}</h1>
        <p className="mt-1 text-sm text-fg-muted">{isPlatform ? t("platformChoose") : t("chooseClinicDescription")}</p>
        {error ? (
          <Alert tone="danger" className="mt-4">
            {error}
          </Alert>
        ) : null}
        <ul className="mt-6 flex flex-col gap-2">
          {clinics.map((c) => (
            <li key={c.id}>
              <Button
                variant="outline"
                className="w-full justify-start"
                loading={switching === c.id}
                disabled={switching !== null}
                icon={<Building2 className="size-4" aria-hidden="true" />}
                onClick={async () => {
                  setSwitching(c.id);
                  setError(null);
                  try {
                    await api.auth.switchTenant(c.id);
                  } catch (err) {
                    setError(isApiError(err) ? err.message : t("switchFailed"));
                    setSwitching(null);
                  }
                }}
              >
                {c.name}
              </Button>
            </li>
          ))}
        </ul>
        <div className="mt-6 flex flex-wrap justify-end gap-2">
          {securityLink}
          {logoutButton}
        </div>
      </div>
    </main>
  );
}

/**
 * A suspended clinic (seen in /auth/me or in any call refused with TENANT_SUSPENDED) gets one clear screen instead
 * of a page full of failed requests. The API already refuses every clinic request; this is presentation only.
 */
function ClinicAccessGate({ children }: { children: ReactNode }) {
  const t = useTranslations("shell");
  const { logout, tenantSuspended } = useAuth();
  const { access } = useClinic();
  if (!tenantSuspended && access !== "Suspended") return <>{children}</>;
  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <EmptyState
        icon={<Building2 className="size-6" />}
        title={t("suspendedTitle")}
        description={t("suspendedDescription")}
        action={
          <Button variant="outline" onClick={() => void logout()} icon={<LogOut className="size-4" aria-hidden="true" />}>
            {t("logout")}
          </Button>
        }
      />
    </main>
  );
}

function ShellLayout({ children }: { children: ReactNode }) {
  const t = useTranslations("shell");
  const [navOpen, setNavOpen] = useState(false);

  return (
    <div className="min-h-dvh">
      <a
        href="#main"
        className="sr-only z-50 rounded-control bg-primary px-4 py-2 text-primary-fg focus:not-sr-only focus:fixed focus:start-4 focus:top-4 print:hidden"
      >
        {t("skipToContent")}
      </a>

      <aside className="fixed inset-y-0 start-0 z-30 hidden w-(--sidebar-w) flex-col border-e border-border bg-surface lg:flex print:hidden">
        <SidebarContent />
      </aside>

      <MobileNav open={navOpen} onClose={() => setNavOpen(false)} />

      <div className="flex min-h-dvh min-w-0 flex-col lg:ps-(--sidebar-w) print:min-h-0 print:ps-0">
        <Topbar onOpenNav={() => setNavOpen(true)} />
        <EmailBanner />
        <ReadOnlyBanner />
        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-(--content-max) min-w-0 flex-1 px-4 py-6 sm:px-6 lg:px-8 print:max-w-none print:p-0">
          {children}
        </main>
      </div>
      <UpgradeDialog />
    </div>
  );
}

function Brand() {
  const t = useTranslations("app");
  return (
    <Link href={HOME_PATH} className="flex items-center gap-2 rounded-control text-primary-strong">
      <span className="flex size-9 items-center justify-center rounded-control bg-primary text-primary-fg" aria-hidden="true">
        <Stethoscope className="size-5" />
      </span>
      <span className="text-base font-bold">{t("name")}</span>
    </Link>
  );
}

function SidebarContent({ onNavigate }: { onNavigate?: () => void }) {
  const t = useTranslations("nav");
  const tShell = useTranslations("shell");
  const { canAny } = useAuth();
  const pathname = usePathname();
  const items = visibleNavItems(canAny);

  return (
    <>
      <div className="flex h-(--topbar-h) shrink-0 items-center border-b border-border px-5">
        <Brand />
      </div>
      <nav aria-label={tShell("mainNav")} className="flex-1 overflow-y-auto px-3 py-4">
        <ul className="flex flex-col gap-1">
          {items.map((item) => {
            const active = pathname === item.href || pathname.startsWith(`${item.href}/`);
            const Icon = item.icon;
            return (
              <li key={item.href}>
                <Link
                  href={item.href}
                  onClick={onNavigate}
                  aria-current={active ? "page" : undefined}
                  className={cn(
                    "flex items-center gap-3 rounded-control px-3 py-2.5 text-sm font-medium transition-colors",
                    active ? "bg-primary-soft text-primary-strong" : "text-fg-muted hover:bg-surface-muted hover:text-fg",
                  )}
                >
                  <Icon className="size-5 shrink-0" aria-hidden="true" />
                  {t(item.labelKey)}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
    </>
  );
}

/** Off-canvas navigation for < lg screens, on a native modal <dialog> (focus trap + Esc for free). */
function MobileNav({ open, onClose }: { open: boolean; onClose: () => void }) {
  const t = useTranslations("common");
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(e) => e.target === ref.current && onClose()}
      className="m-0 me-auto h-dvh max-h-dvh w-72 max-w-[85vw] bg-surface p-0 shadow-floating backdrop:bg-overlay lg:hidden"
    >
      <div className="flex h-full flex-col">
        <div className="absolute end-2 top-3">
          <Button variant="ghost" size="icon" onClick={onClose} aria-label={t("close")}>
            <X className="size-5" aria-hidden="true" />
          </Button>
        </div>
        {open ? <SidebarContent onNavigate={onClose} /> : null}
      </div>
    </dialog>
  );
}

function Topbar({ onOpenNav }: { onOpenNav: () => void }) {
  const t = useTranslations("shell");
  const { session, logout } = useAuth();
  const pathname = usePathname();

  return (
    <header className="sticky top-0 z-20 flex h-(--topbar-h) shrink-0 items-center gap-2 border-b border-border bg-surface/95 px-3 backdrop-blur sm:px-6 print:hidden">
      <Button variant="ghost" size="icon" className="lg:hidden" onClick={onOpenNav} aria-label={t("openMenu")}>
        <Menu className="size-5" aria-hidden="true" />
      </Button>
      <ClinicSwitcher />
      <div className="flex-1" />
      <LocaleSwitcher />
      <Link
        href="/account"
        aria-current={pathname === "/account" ? "page" : undefined}
        className="hidden min-w-0 flex-col items-end rounded-control px-2 py-1 text-end hover:bg-surface-muted sm:flex"
      >
        <span className="max-w-48 truncate text-sm font-medium text-fg">
          <span className="sr-only">{t("account")}: </span>
          {session?.user.fullName}
        </span>
        <span className="max-w-48 truncate text-xs text-fg-subtle" dir="ltr">
          {session?.user.email}
        </span>
      </Link>
      <Link
        href="/account"
        aria-label={t("account")}
        title={t("account")}
        aria-current={pathname === "/account" ? "page" : undefined}
        className="flex size-10 items-center justify-center rounded-control text-fg-muted hover:bg-surface-muted hover:text-fg sm:hidden"
      >
        <UserRound className="size-5" aria-hidden="true" />
      </Link>
      <Button
        variant="ghost"
        size="icon"
        onClick={() => void logout()}
        aria-label={t("logout")}
        title={t("logout")}
      >
        <LogOut className="size-5 rtl:rotate-180" aria-hidden="true" />
      </Button>
    </header>
  );
}

/** Lists the user's clinics; switching issues a new token for the chosen tenant (server-side membership check). */
function ClinicSwitcher() {
  const t = useTranslations("shell");
  const toast = useToast();
  const router = useRouter();
  const { session, api } = useAuth();
  const tenants = useQuery(session ? `tenants:${session.user.id}` : null, () => api.auth.tenants());
  const [switching, setSwitching] = useState(false);
  const current = session?.tenant;

  if (!current) return null;
  const list = tenants.data ?? [];

  if (list.length <= 1) {
    return (
      <div className="flex min-w-0 items-center gap-2 text-sm font-semibold text-fg">
        <Building2 className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
        <span className="truncate">{current.name}</span>
      </div>
    );
  }

  return (
    <div className="flex min-w-0 items-center gap-2">
      <Building2 className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
      <label htmlFor="clinic-switcher" className="sr-only">
        {t("switchClinic")}
      </label>
      <select
        id="clinic-switcher"
        value={current.id}
        disabled={switching}
        onChange={async (e) => {
          setSwitching(true);
          try {
            await api.auth.switchTenant(e.target.value);
            router.push(HOME_PATH);
          } catch (err) {
            toast({ tone: "danger", title: isApiError(err) ? err.message : t("switchFailed") });
          } finally {
            setSwitching(false);
          }
        }}
        className="h-9 max-w-[45vw] min-w-0 truncate rounded-control border border-border-input bg-surface px-2 pe-7 text-sm font-semibold sm:max-w-64"
      >
        {list.map((tenant) => (
          <option key={tenant.id} value={tenant.id}>
            {tenant.name}
          </option>
        ))}
      </select>
    </div>
  );
}

/** Subscription period ended: everything stays readable, every change is refused by the API until renewal. */
function ReadOnlyBanner() {
  const t = useTranslations("shell");
  const { access } = useClinic();
  if (access !== "ReadOnly") return null;
  return (
    <div className="px-4 pt-4 sm:px-6 lg:px-8 print:hidden">
      <Alert tone="warning" title={t("readOnlyTitle")}>
        {t("readOnlyDescription")}
      </Alert>
    </div>
  );
}

function EmailBanner() {
  const t = useTranslations("shell");
  const toast = useToast();
  const { session, api } = useAuth();
  const [sending, setSending] = useState(false);
  if (!session || session.user.emailConfirmed) return null;

  return (
    <div className="px-4 pt-4 sm:px-6 lg:px-8 print:hidden">
      <Alert
        tone="warning"
        title={t("verifyEmailTitle")}
        action={
          <Button
            size="sm"
            variant="outline"
            loading={sending}
            onClick={async () => {
              setSending(true);
              try {
                await api.auth.resendVerification();
                toast({ tone: "success", title: t("verifyEmailSent") });
              } catch (err) {
                toast({ tone: "danger", title: isApiError(err) ? err.message : t("switchFailed") });
              } finally {
                setSending(false);
              }
            }}
          >
            {t("resendVerification")}
          </Button>
        }
      >
        {t("verifyEmailBody")}
      </Alert>
    </div>
  );
}
