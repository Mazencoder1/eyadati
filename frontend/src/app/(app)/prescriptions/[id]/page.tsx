import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PrescriptionDocumentPage } from "@/features/prescriptions/prescription-document";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("prescriptions");
  return { title: t("doc.title") };
}

export default function Page() {
  return <PrescriptionDocumentPage />;
}
