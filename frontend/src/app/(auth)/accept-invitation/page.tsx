import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { Suspense } from "react";
import { AcceptInvitation } from "@/features/auth/accept-invitation";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("auth.invitation");
  return { title: t("title") };
}

export default function Page() {
  return (
    <Suspense>
      <AcceptInvitation />
    </Suspense>
  );
}
