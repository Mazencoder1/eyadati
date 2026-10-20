import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PatientsList } from "@/features/patients/patients-list";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("patients");
  return { title: t("title") };
}

export default function Page() {
  return <PatientsList />;
}
