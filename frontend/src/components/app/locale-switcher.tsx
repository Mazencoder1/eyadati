"use client";

import { Languages } from "lucide-react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useTransition } from "react";
import { localeCookie, type Locale } from "@/i18n/config";
import { cn } from "@/lib/utils";

/**
 * Switches ar ⇄ en. The choice is a plain preference cookie; the server re-renders with the new dir/lang.
 * The accessible name is the visible language name, preceded by a screen-reader-only hint (GD-06: no aria-label that
 * would hide the visible text from voice-control users).
 */
export function LocaleSwitcher({ className }: { className?: string }) {
  const t = useTranslations("common");
  const locale = useLocale();
  const router = useRouter();
  const [pending, startTransition] = useTransition();
  const next: Locale = locale === "ar" ? "en" : "ar";

  return (
    <button
      type="button"
      disabled={pending}
      onClick={() => {
        document.cookie = `${localeCookie}=${next}; path=/; max-age=31536000; samesite=lax`;
        startTransition(() => router.refresh());
      }}
      className={cn(
        "inline-flex h-9 items-center gap-1.5 rounded-control px-2.5 text-sm text-fg-muted hover:bg-surface-muted hover:text-fg",
        className,
      )}
    >
      <Languages className="size-4" aria-hidden="true" />
      <span className="sr-only">{t("switchLanguage")}: </span>
      <span lang={next}>{next === "ar" ? "العربية" : "English"}</span>
    </button>
  );
}
