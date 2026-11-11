#!/usr/bin/env bash

set -euo pipefail

operation="$1"
module="$2"
migration_name="${3:-}"

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
solution_root="$repo_root/DotnetBackEnd/solution"
startup_project="src/Hosts/CSSApps.Api"

supported_modules=(users messaging realestate workflows)

print_usage() {
  echo "Usage:"
  echo "  bash scripts/dev/ef-module-migration.sh add <users|messaging|realestate|workflows|all> <MigrationName>"
  echo "  bash scripts/dev/ef-module-migration.sh add-and-update <users|messaging|realestate|workflows|all> <MigrationName>"
  echo "  bash scripts/dev/ef-module-migration.sh update <users|messaging|realestate|workflows|all>"
}

if [[ $# -lt 2 ]]; then
  print_usage
  exit 1
fi

resolve_module_settings() {
  local target_module="$1"

  case "$target_module" in
    users)
      project="src/Modules/Users/CSSApps.Users.Infrastructure"
      context="UsersDbContext"
      ;;
    messaging)
      project="src/Modules/Messaging/CSSApps.Messaging.Infrastructure"
      context="MessagingDbContext"
      ;;
    realestate)
      project="src/Modules/RealEstate/CSSApps.RealEstate.Infrastructure"
      context="RealEstateDbContext"
      ;;
    workflows)
      project="src/Shared/Modules/CSSApps.Shared.Workflows"
      context="WorkflowsDbContext"
      ;;
    *)
      echo "Unsupported module '$target_module'. Expected one of: ${supported_modules[*]} or 'all' for the update operation."
      exit 1
      ;;
  esac
}

run_database_update() {
  local target_module="$1"

  resolve_module_settings "$target_module"

  echo "Applying EF migrations to the database for module '$target_module' using context '$context'..."
  "${ef_command[@]}" database update \
    --project "$project" \
    --startup-project "$startup_project" \
    --context "$context"
}

run_add_migration() {
  local target_module="$1"
  local target_migration_name="$2"

  resolve_module_settings "$target_module"

  echo "Adding EF migration '$target_migration_name' for module '$target_module' using context '$context'..."
  "${ef_command[@]}" migrations add "$target_migration_name" \
    --project "$project" \
    --startup-project "$startup_project" \
    --context "$context"
}

case "$operation" in
  add|add-and-update)
    if [[ -z "$migration_name" ]]; then
      echo "Migration name cannot be empty."
      print_usage
      exit 1
    fi

    if [[ "$module" != "all" ]]; then
      resolve_module_settings "$module"
    fi
    ;;
  update)
    if [[ -n "$migration_name" ]]; then
      echo "Operation 'update' does not accept a migration name."
      print_usage
      exit 1
    fi
    ;;
  *)
    echo "Unsupported operation '$operation'. Expected 'add', 'add-and-update', or 'update'."
    print_usage
    exit 1
    ;;
esac

cd "$solution_root"

if command -v dotnet-ef >/dev/null 2>&1; then
  ef_command=(dotnet-ef)
elif [[ -x "$HOME/.dotnet/tools/dotnet-ef" ]]; then
  ef_command=("$HOME/.dotnet/tools/dotnet-ef")
else
  echo "dotnet-ef was not found. Install it globally, for example:"
  echo "  dotnet tool update --global dotnet-ef --version 10.0.5"
  exit 1
fi

if [[ "$operation" == "update" ]]; then
  if [[ "$module" == "all" ]]; then
    for target_module in "${supported_modules[@]}"; do
      run_database_update "$target_module"
    done
  else
    run_database_update "$module"
  fi
else
  if [[ "$module" == "all" ]]; then
    for target_module in "${supported_modules[@]}"; do
      run_add_migration "$target_module" "$migration_name"

      if [[ "$operation" == "add-and-update" ]]; then
        run_database_update "$target_module"
      fi
    done
  else
    run_add_migration "$module" "$migration_name"

    if [[ "$operation" == "add-and-update" ]]; then
      run_database_update "$module"
    fi
  fi
fi

echo "Done."