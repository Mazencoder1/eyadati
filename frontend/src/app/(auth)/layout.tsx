import { Stethoscope } from "lucide-react";
import { getTranslations } from "next-intl/server";
import { LocaleSwitcher } from "@/components/app/locale-switcher";

export default async function AuthLayout({ children }: { children: React.ReactNode }) {
  const t = await getTranslations("app");
  return (
    <div className="flex min-h-dvh flex-col">
      <header className="flex items-center justify-between px-4 py-4 sm:px-8">
        <div className="flex items-center gap-2 text-primary-strong">
          <span className="flex size-9 items-center justify-center rounded-control bg-primary text-primary-fg" aria-hidden="true">
            <Stethoscope className="size-5" />
          </span>
          <span className="text-lg font-bold">{t("name")}</span>
        </div>
        <LocaleSwitcher />
      </header>
      <main className="flex flex-1 items-start justify-center px-4 pt-4 pb-12 sm:items-center sm:pt-0">{children}</main>
    </div>
  );
}
