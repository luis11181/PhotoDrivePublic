"use client";

import { Grid2x2, Grid3x3, Settings2, Square } from "lucide-react";
import type { ReactNode } from "react";
import { usePreferences, type PhotoGridDensity } from "@/lib/preferences";
import type { CatalogTag } from "@/lib/types";

export function PhotoGridDensityControl({
  value,
  onChange,
}: {
  value: PhotoGridDensity;
  onChange: (density: PhotoGridDensity) => void;
}) {
  const { t } = usePreferences();
  const options = [
    {
      value: "compact" as const,
      label: t("library.gridCompact"),
      icon: <Grid3x3 />,
    },
    {
      value: "standard" as const,
      label: t("library.gridStandard"),
      icon: <Grid2x2 />,
    },
    {
      value: "comfortable" as const,
      label: t("library.gridComfortable"),
      icon: <Square />,
    },
  ];
  return (
    <div
      className="photo-grid-density"
      role="group"
      aria-label={t("library.gridDensity")}
    >
      {options.map((option) => (
        <button
          key={option.value}
          className={value === option.value ? "active" : ""}
          type="button"
          aria-label={option.label}
          aria-pressed={value === option.value}
          title={option.label}
          onClick={() => onChange(option.value)}
        >
          {option.icon}
        </button>
      ))}
    </div>
  );
}

export function FilterGroup({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: Array<[string, string]>;
  onChange: (value: string) => void;
}) {
  return (
    <fieldset className="filter-group">
      <legend>{label}</legend>
      {options.map(([optionValue, optionLabel]) => (
        <button
          key={optionValue}
          className={value === optionValue ? "active" : ""}
          onClick={() => onChange(optionValue)}
          type="button"
        >
          {optionLabel}
        </button>
      ))}
    </fieldset>
  );
}

export function TagFilterControl({
  tags,
  value,
  onChange,
  onManage,
}: {
  tags: CatalogTag[];
  value: string;
  onChange: (value: string) => void;
  onManage: () => void;
}) {
  const { t } = usePreferences();
  if (tags.length === 0) return null;
  return (
    <div className="tag-filter-control">
      <label className="filter-select tag-filter-select">
        <span>{t("library.tag")}</span>
        <select
          value={value}
          onChange={(event) => onChange(event.target.value)}
        >
          <option value="">{t("library.allTags")}</option>
          {tags.map((tag) => (
            <option key={tag.id} value={tag.id}>
              {tag.name}
            </option>
          ))}
        </select>
      </label>
      <button
        className="icon-button tag-manage-button"
        type="button"
        onClick={onManage}
        aria-label={t("tags.manage")}
        title={t("tags.manage")}
      >
        <Settings2 size={16} />
      </button>
    </div>
  );
}

export function LibraryEmptyState({
  icon,
  title,
  copy,
  action,
  actionLabel,
}: {
  icon: ReactNode;
  title: string;
  copy: string;
  action?: () => void;
  actionLabel?: string;
}) {
  return (
    <div className="empty">
      {icon}
      <h3>{title}</h3>
      <p>{copy}</p>
      {action && (
        <button className="secondary" onClick={action}>
          {actionLabel ?? "Try again"}
        </button>
      )}
    </div>
  );
}
