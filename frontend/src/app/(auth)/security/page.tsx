import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { SecurityPage } from "@/features/account/security-page";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("security");
  return { title: t("title") };
}

export default function Page() {
  return <SecurityPage />;
}
