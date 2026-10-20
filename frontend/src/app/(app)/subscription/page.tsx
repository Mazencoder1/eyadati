import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { SubscriptionPage } from "@/features/subscription/subscription-page";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("subscription");
  return { title: t("title") };
}

export default function Page() {
  return <SubscriptionPage />;
}
