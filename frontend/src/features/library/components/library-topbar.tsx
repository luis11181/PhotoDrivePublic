"use client";

import {
  Bell,
  CalendarDays,
  LogOut,
  Menu,
  Search,
  Settings,
  SlidersHorizontal,
  Users,
  X,
} from "lucide-react";
import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { PreferenceControls } from "@/components/preference-controls";
import type { CurrentUser } from "@/lib/auth";
import { usePreferences } from "@/lib/preferences";
import type { TimelineEvent } from "@/lib/types";

export function LibraryTopbar({
  query,
  onQueryChange,
  placeholder,
  onOpenNavigation,
  onOpenAccount,
  onSignOut,
  onToggleNotifications,
  notificationCount,
  notificationsOpen,
  operationRunning,
  user,
  searchDisabled = false,
  showSearch = true,
  alwaysShowMenu = false,
  eventSuggestions = [],
  eventSuggestionsLoading = false,
  onSelectEvent,
}: {
  query: string;
  onQueryChange: (value: string) => void;
  placeholder?: string;
  onOpenNavigation: () => void;
  onOpenAccount?: () => void;
  onSignOut?: () => Promise<void>;
  onToggleNotifications: () => void;
  notificationCount: number;
  notificationsOpen: boolean;
  operationRunning: boolean;
  user?: CurrentUser;
  searchDisabled?: boolean;
  showSearch?: boolean;
  alwaysShowMenu?: boolean;
  eventSuggestions?: TimelineEvent[];
  eventSuggestionsLoading?: boolean;
  onSelectEvent?: (event: TimelineEvent) => void;
}) {
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const [searchFocused, setSearchFocused] = useState(false);
  const [activeSuggestion, setActiveSuggestion] = useState(-1);
  const accountMenuRef = useRef<HTMLDivElement>(null);
  const { locale, t } = usePreferences();
  const effectivePlaceholder = placeholder ?? t("topbar.search");

  useEffect(() => {
    if (!accountMenuOpen) return;

    function closeOnOutsidePointer(event: PointerEvent) {
      if (
        event.target instanceof Node &&
        !accountMenuRef.current?.contains(event.target)
      ) {
        setAccountMenuOpen(false);
      }
    }

    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") setAccountMenuOpen(false);
    }

    document.addEventListener("pointerdown", closeOnOutsidePointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [accountMenuOpen]);

  return (
    <header className="topbar">
      <button
        className={`icon-button menu-button ${alwaysShowMenu ? "menu-button-always-visible" : ""}`}
        onClick={onOpenNavigation}
        aria-label={t("topbar.openNavigation")}
      >
        <Menu />
      </button>
      {showSearch && <div className="search">
        <Search size={19} />
        <input
          disabled={searchDisabled}
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          placeholder={effectivePlaceholder}
          aria-label={effectivePlaceholder}
          role={onSelectEvent ? "combobox" : undefined}
          aria-autocomplete={onSelectEvent ? "list" : undefined}
          aria-expanded={onSelectEvent ? searchFocused && eventSuggestions.length > 0 : undefined}
          aria-controls={onSelectEvent ? "timeline-event-suggestions" : undefined}
          aria-activedescendant={activeSuggestion >= 0 ? `timeline-event-suggestion-${activeSuggestion}` : undefined}
          onFocus={() => setSearchFocused(true)}
          onBlur={() => window.setTimeout(() => setSearchFocused(false), 120)}
          onKeyDown={(event) => {
            if (!onSelectEvent || eventSuggestions.length === 0) return;
            if (event.key === "ArrowDown") {
              event.preventDefault();
              setActiveSuggestion((current) => Math.min(eventSuggestions.length - 1, current + 1));
            } else if (event.key === "ArrowUp") {
              event.preventDefault();
              setActiveSuggestion((current) => Math.max(0, current - 1));
            } else if (event.key === "Enter" && activeSuggestion >= 0) {
              event.preventDefault();
              onSelectEvent(eventSuggestions[activeSuggestion]);
              setSearchFocused(false);
            } else if (event.key === "Escape") {
              setSearchFocused(false);
              setActiveSuggestion(-1);
            }
          }}
        />
        {query && (
          <button
            onClick={() => onQueryChange("")}
            aria-label={t("topbar.clearFilter")}
          >
            <X size={16} />
          </button>
        )}
        {onSelectEvent && searchFocused && (eventSuggestionsLoading || eventSuggestions.length > 0) && (
          <div className="event-suggestions" id="timeline-event-suggestions" role="listbox" aria-label={t("events.searchGroup")}>
            <p>{t("events.searchGroup")}</p>
            {eventSuggestions.map((timelineEvent, index) => (
              <button
                id={`timeline-event-suggestion-${index}`}
                key={timelineEvent.id}
                type="button"
                role="option"
                aria-selected={activeSuggestion === index}
                className={activeSuggestion === index ? "active" : undefined}
                onMouseDown={(pointerEvent) => pointerEvent.preventDefault()}
                onMouseEnter={() => setActiveSuggestion(index)}
                onClick={() => {
                  onSelectEvent(timelineEvent);
                  setSearchFocused(false);
                }}
              >
                <CalendarDays />
                <span>
                  <strong>{timelineEvent.title}</strong>
                  <small>
                    {formatEventDates(
                      timelineEvent,
                      t("events.timelineEvent"),
                      locale,
                    )}
                  </small>
                  {timelineEvent.description && <em>{timelineEvent.description}</em>}
                </span>
                <b>{t("events.timelineEvent")}</b>
              </button>
            ))}
          </div>
        )}
      </div>}
      <div className="topbar-actions">
        <PreferenceControls className="topbar-preferences" />
        <button
          className={`icon-button notification-button ${notificationsOpen ? "active" : ""}`}
          onClick={onToggleNotifications}
          aria-label={t("topbar.notifications")}
          aria-expanded={notificationsOpen}
        >
          <Bell className={operationRunning ? "notification-bell-running" : ""} />
          {notificationCount > 0 && (
            <span className="notification-badge">
              {notificationCount > 9 ? "9+" : notificationCount}
            </span>
          )}
        </button>
        <div className="account-menu-wrap" ref={accountMenuRef}>
          <button
            className="account-trigger"
            aria-controls="account-menu"
            aria-expanded={accountMenuOpen}
            aria-haspopup="menu"
            aria-label={t("topbar.accountMenu")}
            onClick={() => setAccountMenuOpen((open) => !open)}
            title={user?.displayName}
          >
            <span className="account-trigger-name">
              {user?.displayName ?? "Media Explorer"}
            </span>
            <span className="account-avatar">{avatarLetters(user?.displayName)}</span>
          </button>
          {accountMenuOpen && (
            <div className="account-menu" id="account-menu" role="menu">
              <div className="account-menu-profile">
                <span className="account-avatar account-menu-avatar">
                  {avatarLetters(user?.displayName)}
                </span>
                <span>
                  <strong>{user?.displayName ?? "Media Explorer"}</strong>
                  {user?.email && <small>{user.email}</small>}
                </span>
                {user?.isOwner && <b>{t("topbar.owner")}</b>}
              </div>
              {onOpenAccount && (
                <button
                  className="account-menu-item"
                  onClick={() => {
                    setAccountMenuOpen(false);
                    onOpenAccount();
                  }}
                  role="menuitem"
                  type="button"
                >
                  <Settings /> {t("topbar.accountSettings")}
                </button>
              )}
              {user?.isOwner && (
                <Link
                  className="account-menu-item"
                  href="/users"
                  onClick={() => setAccountMenuOpen(false)}
                  role="menuitem"
                >
                  <Users /> {t("sidebar.familyAccounts")}
                </Link>
              )}
              {user?.isOwner && (
                <Link
                  className="account-menu-item"
                  href="/developer"
                  onClick={() => setAccountMenuOpen(false)}
                  role="menuitem"
                >
                  <SlidersHorizontal /> {t("sidebar.developerTools")}
                </Link>
              )}
              {onSignOut && (
                <>
                  <div className="account-menu-divider" />
                  <button
                    className="account-menu-item account-menu-signout"
                    onClick={() => {
                      setAccountMenuOpen(false);
                      void onSignOut();
                    }}
                    role="menuitem"
                    type="button"
                  >
                    <LogOut /> {t("topbar.signOut")}
                  </button>
                </>
              )}
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

function formatEventDates(
  event: TimelineEvent,
  label: string,
  locale: "en" | "es",
) {
  const formatter = new Intl.DateTimeFormat(
    locale === "es" ? "es-ES" : "en-US",
    {
      day: "numeric",
      month: "short",
      year: "numeric",
      timeZone: "UTC",
    },
  );
  const start = formatter.format(new Date(`${event.startDate}T00:00:00Z`));
  const range = event.endDate
    ? `${start} – ${formatter.format(new Date(`${event.endDate}T00:00:00Z`))}`
    : start;
  return `${label} · ${range}`;
}

function avatarLetters(name?: string) {
  return Array.from(name?.trim() ?? "")
    .slice(0, 2)
    .join("")
    .toUpperCase() || "ME";
}
