"""Run the backend tests against a disposable database migrated by Liquibase."""

import os
from pathlib import Path
import shlex
import subprocess
import uuid


SERVICE = Path(__file__).resolve().parents[1]
ROOT = SERVICE.parent
DATABASE = "expenses_users_check_" + uuid.uuid4().hex[:12]
DOCKER = ["docker", "--context", "desktop-linux", "exec", "-i", "expenses-postgres"]


def run(args, *, cwd=SERVICE, env=None, input=None):
    result = subprocess.run(args, cwd=cwd, env=env, input=input, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(f"{args[0]} failed:\n{result.stdout}\n{result.stderr}")
    return result.stdout


def connection_value(value):
    return '"' + value.replace('"', '""') + '"'


def main():
    local = {}
    env_file = ROOT / "expenses-infrastructure" / ".env"
    if env_file.exists():
        for line in env_file.read_text().splitlines():
            if line.strip() and not line.lstrip().startswith("#"):
                key, raw = line.split("=", 1)
                values = shlex.split(raw, comments=False)
                local[key.strip()] = values[0] if values else ""
    username = os.environ.get("POSTGRES_USER", local.get("POSTGRES_USER", "admin"))
    password = os.environ.get("POSTGRES_PASSWORD", local.get("POSTGRES_PASSWORD"))
    if not password:
        raise RuntimeError("Configure POSTGRES_PASSWORD in the environment or expenses-infrastructure/.env.")
    env = os.environ.copy()
    env.update(
        LIQUIBASE_COMMAND_URL=f"jdbc:postgresql://127.0.0.1:5432/{DATABASE}",
        LIQUIBASE_COMMAND_USERNAME=username,
        LIQUIBASE_COMMAND_PASSWORD=password,
        EXPENSES_TEST_CONNECTION_STRING=(
            f"Host=127.0.0.1;Port=5432;Database={DATABASE};"
            f"Username={connection_value(username)};Password={connection_value(password)};Timeout=5"
        ),
    )
    created = False
    try:
        run(DOCKER + ["createdb", "-U", username, DATABASE])
        created = True
        print(f"Temporary database: {DATABASE}", flush=True)
        run(["liquibase", "--defaults-file=config/liquibase.properties", "update"],
            cwd=ROOT / "expenses-liquibase", env=env)
        print("Liquibase schema applied; running backend tests.", flush=True)
        print(run(["dotnet", "test", "Expenses.sln", "--no-restore"], env=env), flush=True)
    finally:
        if created:
            assert DATABASE.startswith("expenses_users_check_") and DATABASE != "expenses"
            run(DOCKER + ["dropdb", "-U", username, DATABASE])
            print("Temporary database removed; no test data was inserted into expenses.", flush=True)


if __name__ == "__main__":
    main()
