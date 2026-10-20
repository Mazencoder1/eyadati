"use client";

import { useId, type ComponentProps, type ReactNode } from "react";
import { cn } from "@/lib/utils";

/** Shared props for labelled form controls. `error` is an already-localized message. */
export interface FieldProps {
  label: ReactNode;
  hint?: ReactNode;
  error?: string | null;
  required?: boolean;
  /** Visually hide the label (still read by screen readers). */
  hideLabel?: boolean;
  className?: string;
}

const controlBase =
  "block w-full min-w-0 rounded-control border border-border-input bg-surface px-3 text-sm text-fg placeholder:text-fg-subtle " +
  "transition-colors focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-focus " +
  "disabled:cursor-not-allowed disabled:bg-surface-muted disabled:text-fg-subtle read-only:border-border read-only:bg-surface-muted " +
  "aria-[invalid=true]:border-danger";

function FieldShell({
  id,
  label,
  hint,
  error,
  required,
  hideLabel,
  className,
  children,
}: FieldProps & { id: string; children: ReactNode }) {
  return (
    <div className={cn("flex min-w-0 flex-col gap-1.5", className)}>
      <label htmlFor={id} className={cn("text-sm font-medium text-fg", hideLabel && "sr-only")}>
        {label}
        {required ? (
          <span className="text-danger" aria-hidden="true">
            {" "}
            *
          </span>
        ) : null}
      </label>
      {children}
      {hint && !error ? (
        <p id={`${id}-hint`} className="text-xs text-fg-subtle">
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={`${id}-error`} className="text-xs font-medium text-danger-strong">
          {error}
        </p>
      ) : null}
    </div>
  );
}

function describedBy(id: string, hint: unknown, error: unknown) {
  if (error) return `${id}-error`;
  if (hint) return `${id}-hint`;
  return undefined;
}

export type InputProps = FieldProps & Omit<ComponentProps<"input">, "className"> & { inputClassName?: string };

export function Input({ label, hint, error, required, hideLabel, className, inputClassName, id, ...props }: InputProps) {
  const autoId = useId();
  const controlId = id ?? autoId;
  return (
    <FieldShell {...{ id: controlId, label, hint, error, required, hideLabel, className }}>
      <input
        id={controlId}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(controlId, hint, error)}
        className={cn(controlBase, "h-10", inputClassName)}
        {...props}
      />
    </FieldShell>
  );
}

export type TextareaProps = FieldProps & Omit<ComponentProps<"textarea">, "className">;

export function Textarea({ label, hint, error, required, hideLabel, className, id, rows = 3, ...props }: TextareaProps) {
  const autoId = useId();
  const controlId = id ?? autoId;
  return (
    <FieldShell {...{ id: controlId, label, hint, error, required, hideLabel, className }}>
      <textarea
        id={controlId}
        rows={rows}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(controlId, hint, error)}
        className={cn(controlBase, "py-2 leading-relaxed")}
        {...props}
      />
    </FieldShell>
  );
}

export interface SelectOption {
  value: string;
  label: string;
  disabled?: boolean;
}

export type SelectProps = FieldProps &
  Omit<ComponentProps<"select">, "className" | "children"> & {
    options: SelectOption[];
    placeholder?: string;
  };

/** Native <select>: fully keyboard/screen-reader accessible and mobile-friendly out of the box. */
export function Select({
  label,
  hint,
  error,
  required,
  hideLabel,
  className,
  id,
  options,
  placeholder,
  ...props
}: SelectProps) {
  const autoId = useId();
  const controlId = id ?? autoId;
  return (
    <FieldShell {...{ id: controlId, label, hint, error, required, hideLabel, className }}>
      <select
        id={controlId}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(controlId, hint, error)}
        className={cn(controlBase, "h-10 pe-8")}
        {...props}
      >
        {placeholder !== undefined ? <option value="">{placeholder}</option> : null}
        {options.map((o) => (
          <option key={o.value} value={o.value} disabled={o.disabled}>
            {o.label}
          </option>
        ))}
      </select>
    </FieldShell>
  );
}

export interface CheckboxProps extends Omit<ComponentProps<"input">, "type" | "className"> {
  label: ReactNode;
  description?: ReactNode;
  className?: string;
}

export function Checkbox({ label, description, className, id, ...props }: CheckboxProps) {
  const autoId = useId();
  const controlId = id ?? autoId;
  return (
    <div className={cn("flex items-start gap-2.5", className)}>
      <input
        id={controlId}
        type="checkbox"
        className="mt-1 size-4 shrink-0 cursor-pointer accent-[var(--c-primary)] disabled:cursor-not-allowed"
        aria-describedby={description ? `${controlId}-desc` : undefined}
        {...props}
      />
      <div className="min-w-0">
        <label htmlFor={controlId} className="cursor-pointer text-sm text-fg">
          {label}
        </label>
        {description ? (
          <p id={`${controlId}-desc`} className="text-xs text-fg-subtle">
            {description}
          </p>
        ) : null}
      </div>
    </div>
  );
}

/** Groups related checkboxes/radios with a legend. */
export function Fieldset({ legend, children, className }: { legend: ReactNode; children: ReactNode; className?: string }) {
  return (
    <fieldset className={cn("min-w-0 rounded-card border border-border p-4", className)}>
      <legend className="px-1 text-sm font-semibold text-fg">{legend}</legend>
      {children}
    </fieldset>
  );
}
