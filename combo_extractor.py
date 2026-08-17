import os
import re
import sys
from pathlib import Path

# Prefijos reconocidos
URL_PREFIXES = ("url:", "url :", "url=", "host:", "host :", "host=", "site:", "site :", "website:", "target:")
USER_PREFIXES = ("user:", "user :", "user=", "username:", "username :", "login:", "login :", "email:", "email :", "usr:", "usr :", "account:")
PASS_PREFIXES = ("pass:", "pass :", "pass=", "password:", "password :", "pwd:", "pwd :", "clave:", "contraseña:", "contrasena:", "secret:", "key:")
IGNORE_PREFIXES = ("soft:", "browser:", "profile:", "application:", "app:", "title:", "time:", "path:", "created:", "modified:", "========", "--------")

def get_prefix_value(line: str, prefixes: tuple) -> str:
    line_lower = line.lower().strip()
    for prefix in prefixes:
        if line_lower.startswith(prefix):
            return line[len(prefix):].strip()
    return ""

def try_split_user_pass(line: str) -> tuple[str, str] | None:
    line = line.strip()
    if not line or line.startswith(("http://", "https://", "android://")):
        return None
    line_lower = line.lower()
    if any(line_lower.startswith(p) for p in URL_PREFIXES + USER_PREFIXES + PASS_PREFIXES + IGNORE_PREFIXES):
        return None
    if ":" in line:
        parts = line.split(":", 1)
        u, p = parts[0].strip(), parts[1].strip()
        if u and p and " " not in u:
            return u, p
    return None

def is_full_url_combo(line: str) -> str | None:
    line = line.strip()
    if (line.startswith(("http://", "https://", "android://")) and line.count(":") >= 2):
        return line
    return None

def process_lines(lines: list[str]) -> list[str]:
    combos = []
    i = 0
    total = len(lines)

    while i < total:
        line = lines[i].strip()
        if not line:
            i += 1
            continue

        full_combo = is_full_url_combo(line)
        if full_combo:
            combos.append(full_combo)
            i += 1
            continue

        url = None
        user = None
        pwd = None
        lookahead = min(6, total - i)
        last_offset = 0

        for offset in range(lookahead):
            curr = lines[i + offset].strip()
            if not curr:
                continue

            # Buscar URL
            if not url:
                u_val = get_prefix_value(curr, URL_PREFIXES)
                if u_val:
                    url = u_val
                    last_offset = offset
                    continue

            # Buscar User prefix
            if not user:
                us_val = get_prefix_value(curr, USER_PREFIXES)
                if us_val:
                    user = us_val
                    last_offset = offset
                    continue

            # Buscar Pass prefix
            if not pwd:
                p_val = get_prefix_value(curr, PASS_PREFIXES)
                if p_val:
                    pwd = p_val
                    last_offset = offset
                    continue

            # Si no hubo prefijo de usuario, buscar formato directo user:pass
            if not user and not pwd:
                up = try_split_user_pass(curr)
                if up:
                    user, pwd = up
                    last_offset = offset
                    break

            if user and pwd:
                break

        if user and pwd:
            if url:
                combos.append(f"{url}:{user}:{pwd}")
            else:
                combos.append(f"{user}:{pwd}")
            i += max(1, last_offset + 1)
        else:
            i += 1

    return combos

def parse_file(file_path: Path) -> list[str]:
    for encoding in ("utf-8", "latin-1", "utf-16", "cp1252"):
        try:
            with open(file_path, "r", encoding=encoding, errors="ignore") as f:
                return process_lines(f.readlines())
        except Exception:
            continue
    return []

def main():
    target_path = input("Ingresa la ruta de la carpeta o archivo: ").strip().strip('"')
    if not target_path or not os.path.exists(target_path):
        print("❌ Ruta no válida.")
        return

    target = Path(target_path)
    files_to_scan = []

    if target.is_file():
        files_to_scan.append(target)
    else:
        for ext in ("*.txt", "*.log"):
            files_to_scan.extend(target.rglob(ext))

    print(f"🔍 Escaneando {len(files_to_scan)} archivo(s)...")

    unique_combos = set()
    total_found = 0

    for file in files_to_scan:
        combos = parse_file(file)
        total_found += len(combos)
        unique_combos.update(combos)

    output_file = target.parent / "combos_generados.txt" if target.is_file() else target / "combos_generados.txt"

    with open(output_file, "w", encoding="utf-8") as f:
        for combo in sorted(unique_combos):
            f.write(combo + "\n")

    print(f"\n✅ Proceso completado:")
    print(f" - Combos totales encontrados: {total_found}")
    print(f" - Combos únicos guardados: {len(unique_combos)}")
    print(f" - Archivo guardado en: {output_file}")

if __name__ == "__main__":
    main()
