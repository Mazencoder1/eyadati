import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { DoctorsList } from "@/features/doctors/doctor-pages";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("doctors");
  return { title: t("title") };
}

export default function Page() {
  return <DoctorsList />;
}
