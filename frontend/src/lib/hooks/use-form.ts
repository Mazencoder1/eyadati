"use client";

import { useTranslations } from "next-intl";
import { useCallback, useState, type ChangeEvent } from "react";
import { isApiError, type ApiError, type FieldErrors } from "@/lib/api/client";
import type { ValidationCode } from "@/lib/validation/saudi";

type Values = Record<string, string | boolean>;
export type Validator<V> = (values: V) => Partial<Record<keyof V | string, ValidationCode | null>>;

/** First message per field; server keys are camelCase property names (e.g. "mobileNumber", "windows[0]"). */
export function mapServerErrors(errors: FieldErrors | undefined): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, messages] of Object.entries(errors ?? {})) {
    if (messages.length > 0) result[key] = messages[0];
  }
  return result;
}

/**
 * Form state + submit lifecycle:
 * 1. client-side validation (codes → localized messages) gives instant feedback;
 * 2. the request runs; a 400 maps the server's per-field messages onto the same fields;
 * 3. any other API error is exposed as `formError` (its message is already localized by the server).
 * Focus moves to the first invalid field so keyboard and screen-reader users land on the problem.
 */
export function useForm<V extends Values>(initial: V) {
  const tv = useTranslations("validation");
  const [values, setValues] = useState<V>(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<ApiError | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const setField = useCallback(<K extends keyof V>(key: K, value: V[K]) => {
    setValues((prev) => ({ ...prev, [key]: value }));
    setErrors((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key as string];
      return next;
    });
  }, []);

  /** Spread onto <Input>/<Select>/<Textarea>: value, onChange, error. */
  const bind = <K extends keyof V>(key: K) => ({
    name: key as string,
    value: String(values[key] ?? ""),
    onChange: (e: ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) =>
      setField(key, e.target.value as V[K]),
    error: errors[key as string],
  });

  const focusFirstError = () =>
    requestAnimationFrame(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());

  const submit = async (validate: Validator<V> | null, action: (values: V) => Promise<void>): Promise<boolean> => {
    if (validate) {
      const found: Record<string, string> = {};
      for (const [key, code] of Object.entries(validate(values))) {
        if (code) found[key] = tv(code as ValidationCode);
      }
      if (Object.keys(found).length > 0) {
        setErrors(found);
        setFormError(null);
        focusFirstError();
        return false;
      }
    }

    setSubmitting(true);
    setFormError(null);
    try {
      await action(values);
      return true;
    } catch (e) {
      if (isApiError(e)) {
        if (e.errors) {
          setErrors(mapServerErrors(e.errors));
          focusFirstError();
        }
        setFormError(e);
      } else {
        throw e;
      }
      return false;
    } finally {
      setSubmitting(false);
    }
  };

  return { values, setValues, setField, bind, errors, setErrors, formError, setFormError, submitting, submit };
}
