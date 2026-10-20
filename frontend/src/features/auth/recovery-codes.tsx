"use client";

import { Check, Copy } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { Button } from "@/components/ui/button";

/**
 * The one-time recovery codes, shown once (after enrolment and after replacing them). The codes only ever live in
 * component state: they are never written to storage, a URL or the console, and disappear when the view closes.
 */
export function RecoveryCodes({ codes }: { codes: string[] }) {
  const t = useTranslations("auth.mfa.codes");
  const [copied, setCopied] = useState(false);

  const copyAll = async () => {
    try {
      await navigator.clipboard.writeText(codes.join("\n"));
      setCopied(true);
    } catch {
      // Clipboard blocked (insecure context, permissions): the codes are on screen and can be written down.
    }
  };

  return (
    <div className="flex flex-col gap-3">
      <ul
        aria-label={t("listLabel")}
        dir="ltr"
        className="grid grid-cols-1 gap-2 rounded-card border border-border bg-surface-muted p-3 font-mono text-sm sm:grid-cols-2"
      >
        {codes.map((code) => (
          <li key={code} className="tracking-wider">
            {code}
          </li>
        ))}
      </ul>
      <div className="flex items-center gap-3">
        <Button
          variant="outline"
          size="sm"
          onClick={() => void copyAll()}
          icon={copied ? <Check className="size-4" aria-hidden="true" /> : <Copy className="size-4" aria-hidden="true" />}
        >
          {t("copy")}
        </Button>
        <span role="status" className="text-sm text-success-strong">
          {copied ? t("copied") : ""}
        </span>
      </div>
    </div>
  );
}
