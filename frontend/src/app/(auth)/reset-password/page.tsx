import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { Suspense } from "react";
import { SetPasswordForm } from "@/features/auth/password-forms";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("auth.reset");
  return { title: t("title") };
}

export default function Page() {
  return (
    <Suspense>
      <SetPasswordForm />
    </Suspense>
  );
}
