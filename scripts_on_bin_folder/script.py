"""
build_title_ids.py
------------------
Percorre todas as pastas de XUID em <CSV_ROOT>/<XUID>/games.csv
(onde CSV_ROOT = pasta onde este script reside).

Gera dois arquivos na raiz CSV_ROOT:

  - XboxTitleIDs.json
      Array de jogos UNICOS, deduplicados por TitleId.
      Contem APENAS as colunas que sao invariantes por titulo
      (metadados do jogo: nome, devices, devs, generos, totais
      maximos de conquistas etc.). Listas e JSONs sao re-hidratados
      para estruturas reais — quem consome o JSON nao precisa
      mais splitar '|' nem parsear strings.

  - allPlayers.csv
      Tabela de relacionamento jogador <-> jogo, com as colunas
      que VARIAM por jogador para um mesmo titulo (progresso,
      tempo jogado, ultima vez que jogou etc.).

A pasta 'images/' eh ignorada na varredura.
"""

import csv
import json
import os
import sys

# ---------------------------------------------------------------------------
# Configuracao — raiz fixada na pasta deste arquivo
# ---------------------------------------------------------------------------

CSV_ROOT = os.path.dirname(os.path.abspath(__file__))
JSON_OUT = os.path.join(CSV_ROOT, "XboxTitleIDs.json")
PLAYERS_OUT = os.path.join(CSV_ROOT, "allPlayers.csv")
GAMERTAG_OUT = os.path.join(CSV_ROOT, "allPlayers_gamertag.csv")
PROFILE_DIR_NAME = "profilePicture"

# ---------------------------------------------------------------------------
# Schemas — quais colunas vao para o JSON (titulo) e quais para o CSV (player)
# ---------------------------------------------------------------------------

# Invariantes por titulo: idem para qualquer jogador que possua o jogo.
TITLE_COLUMNS = [
    # Identidade
    "TitleId", "Name", "VuiDisplayName", "Type", "MediaItemType",
    "Devices", "IsBundle", "IsStreamable",
    "XboxLiveTier", "XboxLiveGoldRequired",
    # Identificadores secundarios
    "ModernTitleId", "Pfn", "BingId", "ServiceConfigId",
    "WindowsPhoneProductId", "ProductId", "AlternateProductId",
    # Imagem
    "DisplayImage", "DisplayImageHighQuality", "LocalImageFile", "ImageCount",
    # Totais de conquistas (sao limites do jogo, nao do jogador)
    "TotalAchievements", "TotalGamerscore", "AchievementSourceVersion",
    "HasAchievements",
    # GamePass (atributos do titulo)
    "IsGamePass", "HasGamePassDetail",
    # Detail textual
    "DeveloperName", "PublisherName", "ReleaseDate", "MinAge",
    "ShortDescription", "Description",
    "Genres", "Attributes", "Capabilities", "Availabilities",
    # Stats
    "StatsSourceVersion",
    # JSONs opacos preservados
    "ContentBoards", "Images", "TitleRecord", "AlternateTitleIds",
    # Catch-all
    "TitleExtras", "DetailExtras",
]

# Variam por jogador. Vao para allPlayers.csv com TitleId como chave estrangeira.
PLAYER_COLUMNS = [
    "TitleId", "Gamertag", "Xuid", "ScanDate",
    "CurrentAchievements", "CurrentGamerscore", "ProgressPercentage",
    "RemainingGamerscore", "RemainingAchievements",
    "IsCompleted", "CompletionRatio",
    "LastTimePlayed", "DaysSinceLastPlay", "Visible", "CanHide",
    "TimePlayedMinutes", "TimePlayedHours", "TimePlayedFormatted",
    "FriendsWhoPlayed",
]

# ---------------------------------------------------------------------------
# Conversores — transformam strings do CSV em tipos JSON-friendly
# ---------------------------------------------------------------------------

def _maybe_int(v):
    if v is None: return None
    s = str(v).strip()
    if not s: return None
    try: return int(s)
    except ValueError:
        try: return int(float(s))
        except ValueError: return None

def _maybe_float(v):
    if v is None: return None
    s = str(v).strip()
    if not s: return None
    try: return float(s)
    except ValueError: return None

def _maybe_bool(v):
    if v is None: return None
    s = str(v).strip().lower()
    if s in ("true", "1", "yes"): return True
    if s in ("false", "0", "no"): return False
    return None

def _maybe_json(v):
    """Para colunas que guardam JSON serializado (Availabilities, etc.)."""
    if v is None: return None
    s = str(v).strip()
    if not s: return None
    try: return json.loads(s)
    except (json.JSONDecodeError, ValueError):
        return s  # fallback: devolve string crua

def _split_pipe(v):
    """Coluna 'A|B|C' -> ['A','B','C']. Vazio -> []."""
    if v is None: return []
    s = str(v).strip()
    if not s: return []
    return [p for p in s.split("|") if p]

def _str(v):
    return ("" if v is None else str(v)).strip()

# Mapeamento por coluna. Colunas sem entrada -> string como veio do CSV.
TITLE_CONVERTERS = {
    "IsBundle": _maybe_bool,
    "IsStreamable": _maybe_bool,
    "XboxLiveGoldRequired": _maybe_bool,
    "HasAchievements": _maybe_bool,
    "IsGamePass": _maybe_bool,
    "HasGamePassDetail": _maybe_bool,
    "ImageCount": _maybe_int,
    "TotalAchievements": _maybe_int,
    "TotalGamerscore": _maybe_int,
    "AchievementSourceVersion": _maybe_int,
    "StatsSourceVersion": _maybe_int,
    "MinAge": _maybe_int,
    "Devices": _split_pipe,
    "Genres": _split_pipe,
    "Attributes": _split_pipe,
    "Capabilities": _split_pipe,
    "Availabilities": _maybe_json,
    "ContentBoards": _maybe_json,
    "Images": _maybe_json,
    "TitleRecord": _maybe_json,
    "AlternateTitleIds": _maybe_json,
    "TitleExtras": _maybe_json,
    "DetailExtras": _maybe_json,
}

# ---------------------------------------------------------------------------
# Varredura
# ---------------------------------------------------------------------------

games: dict[str, dict] = {}      # TitleId -> objeto do titulo (1a ocorrencia)
player_rows: list[dict] = []     # uma linha por (TitleId, jogador)
players_seen: dict[str, dict] = {}  # Xuid -> {Gamertag, Xuid, GamesCount, ProfilePicture}
processed_folders: list[str] = []
skipped_folders: list[str] = []
title_better_seen: set[str] = set()  # TitleIds para os quais ja substituimos com versao "mais rica"

if not os.path.isdir(CSV_ROOT):
    print(f"[ERRO] Pasta nao encontrada: {CSV_ROOT}")
    sys.exit(1)

def _richness(row: dict) -> int:
    """Score de quao completo um registro de titulo eh. Usado pra preferir
    a versao com mais metadados quando varios jogadores tem o mesmo jogo."""
    score = 0
    for col in ("Description", "ShortDescription", "DeveloperName",
                "PublisherName", "ReleaseDate", "Genres", "Capabilities",
                "Attributes", "Availabilities"):
        if (row.get(col) or "").strip():
            score += 1
    return score

for entry in os.scandir(CSV_ROOT):
    if not entry.is_dir():
        continue
    # Pastas globais criadas pelo Scanner (nao contem games.csv)
    if entry.name.lower() in ("images", PROFILE_DIR_NAME.lower(), "__pycache__"):
        continue

    games_csv = os.path.join(entry.path, "games.csv")
    if not os.path.isfile(games_csv):
        skipped_folders.append(entry.name)
        continue

    processed_folders.append(entry.name)

    # utf-8-sig: o Scanner grava o CSV com BOM (new UTF8Encoding(true))
    with open(games_csv, encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            title_id = (row.get("TitleId") or "").strip()
            if not title_id:
                continue

            # Captura par (Gamertag, Xuid) na primeira linha valida do jogador.
            # O nome da pasta == Xuid, mas confiamos no campo do CSV para a
            # Gamertag (que pode ter espacos/maiusculas canonicas).
            row_xuid = (row.get("Xuid") or entry.name).strip()
            row_gamertag = (row.get("Gamertag") or "").strip()
            if row_xuid and row_xuid not in players_seen:
                pic_rel = f"{PROFILE_DIR_NAME}/{row_xuid}.jpg"
                pic_abs = os.path.join(CSV_ROOT, PROFILE_DIR_NAME, f"{row_xuid}.jpg")
                players_seen[row_xuid] = {
                    "Gamertag": row_gamertag,
                    "Xuid": row_xuid,
                    "ProfilePicture": pic_rel if os.path.isfile(pic_abs) else "",
                }

            # ----- Title-level: 1a ocorrencia OU sobrescreve se a nova
            # for nitidamente mais "rica" (mais metadados nao-vazios) -----
            current = games.get(title_id)
            if current is None:
                title_obj = {}
                for col in TITLE_COLUMNS:
                    raw = row.get(col)
                    conv = TITLE_CONVERTERS.get(col)
                    title_obj[col] = conv(raw) if conv else _str(raw)
                games[title_id] = title_obj
            elif title_id not in title_better_seen:
                # Compara riqueza usando o CSV bruto (antes da conversao)
                # Reconstruir a "row" do registro armazenado nao eh trivial
                # com tipos ja convertidos. Em vez disso comparamos:
                # se a nova linha tem ALGUM campo textual nao-vazio que
                # estava vazio antes, sobrescrevemos a entrada do titulo.
                missing_before = [c for c in (
                    "Description", "ShortDescription", "DeveloperName",
                    "PublisherName", "ReleaseDate", "Genres", "Capabilities",
                    "Attributes", "Availabilities"
                ) if not current.get(c)]
                has_now = [c for c in missing_before if (row.get(c) or "").strip()]
                if has_now:
                    title_obj = {}
                    for col in TITLE_COLUMNS:
                        raw = row.get(col)
                        conv = TITLE_CONVERTERS.get(col)
                        title_obj[col] = conv(raw) if conv else _str(raw)
                    games[title_id] = title_obj
                    title_better_seen.add(title_id)

            # ----- Player-level: sempre adiciona -----
            player_obj = {col: _str(row.get(col, "")) for col in PLAYER_COLUMNS}
            player_rows.append(player_obj)

# ---------------------------------------------------------------------------
# Ordenacao
# ---------------------------------------------------------------------------

def _name_sort_key(name: str) -> str:
    return (name or "").lstrip(" \"'#.!()[]-_").lower()

sorted_games = sorted(
    games.values(),
    key=lambda g: _name_sort_key(g.get("Name", ""))
)

# Sort player_rows por (Gamertag asc, nome do jogo asc)
titles_by_id = {g["TitleId"]: g.get("Name", "") for g in sorted_games}
player_rows.sort(key=lambda r: (
    (r.get("Gamertag", "") or "").lower(),
    _name_sort_key(titles_by_id.get(r.get("TitleId", ""), ""))
))

# ---------------------------------------------------------------------------
# Gravacao
# ---------------------------------------------------------------------------

with open(JSON_OUT, "w", encoding="utf-8") as f:
    json.dump(sorted_games, f, ensure_ascii=False, indent=2)

with open(PLAYERS_OUT, "w", encoding="utf-8-sig", newline="") as f:
    writer = csv.DictWriter(f, fieldnames=PLAYER_COLUMNS,
                            quoting=csv.QUOTE_ALL)
    writer.writeheader()
    for row in player_rows:
        writer.writerow(row)

# allPlayers_gamertag.csv: tabela enxuta Gamertag <-> Xuid (+ caminho da foto)
GAMERTAG_COLUMNS = ["Gamertag", "Xuid", "ProfilePicture"]
sorted_players = sorted(
    players_seen.values(),
    key=lambda p: (p.get("Gamertag", "") or "").lower()
)
with open(GAMERTAG_OUT, "w", encoding="utf-8-sig", newline="") as f:
    writer = csv.DictWriter(f, fieldnames=GAMERTAG_COLUMNS,
                            quoting=csv.QUOTE_ALL)
    writer.writeheader()
    for row in sorted_players:
        writer.writerow(row)

# ---------------------------------------------------------------------------
# Relatorio
# ---------------------------------------------------------------------------

print(f"OK Pastas processadas      : {len(processed_folders)}")
print(f"OK Jogos unicos            : {len(sorted_games)}")
print(f"OK Linhas allPlayers.csv   : {len(player_rows)}")
print(f"OK Jogadores unicos        : {len(sorted_players)}")
pic_count = sum(1 for p in sorted_players if p.get("ProfilePicture"))
print(f"OK Fotos de perfil baixadas: {pic_count}/{len(sorted_players)}")
print(f"OK JSON                : {JSON_OUT}")
print(f"OK allPlayers.csv      : {PLAYERS_OUT}")
print(f"OK allPlayers_gamertag : {GAMERTAG_OUT}")

if skipped_folders:
    print(f"\nAVISO Pastas sem games.csv ({len(skipped_folders)}):")
    for name in skipped_folders:
        print(f"   - {name}")
