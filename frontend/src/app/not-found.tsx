import { getTranslations } from "next-intl/server";
import { ButtonLink } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { HOME_PATH } from "@/lib/routes";

export default async function NotFound() {
  const t = await getTranslations("common");
  return (
    <main className="flex min-h-dvh items-center justify-center p-4">
      <EmptyState
        title={t("notFoundTitle")}
        description={t("notFoundDescription")}
        action={<ButtonLink href={HOME_PATH}>{t("backHome")}</ButtonLink>}
      />
    </main>
  );
}
