using Newtonsoft.Json.Linq;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Wpf.Ui.Common;
using Wpf.Ui.Contracts;
using Wpf.Ui.Controls;

namespace XAU.ViewModels.Pages
{
    public partial class ScannerViewModel : ObservableObject, INavigationAware
    {
        private readonly ISnackbarService _snackbarService;
        private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);

        // Raiz fixa = pasta do executavel. Funciona tanto para build normal quanto
        // para publish single-file/self-contained porque AppContext.BaseDirectory
        // sempre aponta para o diretorio do .exe em runtime.
        private static readonly string AppRoot = AppContext.BaseDirectory;
        private readonly string _playersFilePath = Path.Combine(AppRoot, "players.txt");
        private readonly string _outputFolderPath = Path.Combine(AppRoot, "csv");

        private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(24);

        private readonly SemaphoreSlim _scanLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private System.Threading.Timer? _dailyTimer;
        private System.Threading.Timer? _countdownTimer;
        private DateTime _lastScanAt = DateTime.MinValue;
        private bool _hasEverScanned = false;

        private readonly HttpClient _imageHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        [ObservableProperty] private bool _isInitialized = false;
        [ObservableProperty] private bool _isScanning = false;
        [ObservableProperty] private bool _playersFileFound = false;
        [ObservableProperty] private int _playerCount = 0;
        [ObservableProperty] private int _progressoCurrent = 0;
        [ObservableProperty] private int _totalPlayers = 0;
        [ObservableProperty] private string _currentPlayerName = "";
        [ObservableProperty] private string _statusMessage = "Aguardando...";
        [ObservableProperty] private string _lastScanTime = "Nunca";
        [ObservableProperty] private string _nextScanCountdown = "--:--:--";
        [ObservableProperty] private string _outputPath = "";
        [ObservableProperty] private string _playersFileStatusText = "players.txt: verificando...";
        [ObservableProperty] private string _nextScanText = "Proxima varredura: --:--:--";
        [ObservableProperty] private string _lastScanText = "Ultima varredura: Nunca";
        [ObservableProperty] private string _currentPlayerProgressText = "";
        [ObservableProperty] private Visibility _scanProgressVisibility = Visibility.Collapsed;
        [ObservableProperty] private bool _canStartScan = false;
        [ObservableProperty] private ObservableCollection<string> _logLines = new ObservableCollection<string>();

        public ScannerViewModel(ISnackbarService snackbarService)
        {
            _snackbarService = snackbarService;
            OutputPath = _outputFolderPath;
        }

        public void OnNavigatedTo()
        {
            if (!IsInitialized && HomeViewModel.InitComplete)
                InitializeViewModel();

            RefreshPlayersFileState();

            // Auto-scan apenas se a ultima varredura foi ha >= 24h.
            // Evita disparar milhares de chamadas a API toda vez que o usuario
            // abre a aba do Scanner (causava HTTP 429 / Too Many Requests).
            bool dueForScan = _lastScanAt == DateTime.MinValue
                              || (DateTime.UtcNow - _lastScanAt) >= ScanInterval;

            if (IsInitialized && PlayersFileFound && !_hasEverScanned
                && IsLoggedIn() && dueForScan)
            {
                _ = Task.Run(() => RunScanAsync(_cts.Token));
            }
        }

        public void OnNavigatedFrom()
        {
        }

        private void InitializeViewModel()
        {
            IsInitialized = true;
            OutputPath = _outputFolderPath;

            // Recupera _lastScanAt persistido pra nao re-escanear na inicializacao
            LoadScanState();

            _dailyTimer = new System.Threading.Timer(OnDailyTimerTick, null,
                ScanInterval, ScanInterval);

            _countdownTimer = new System.Threading.Timer(OnCountdownTick, null,
                TimeSpan.Zero, TimeSpan.FromSeconds(1));

            UpdateCanStartScan();
        }

        private string StateFilePath =>
            Path.Combine(_outputFolderPath, "scanner_state.json");

        private void LoadScanState()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return;
                var json = File.ReadAllText(StateFilePath);
                var obj = JObject.Parse(json);
                var ts = obj["lastScanAtUtc"]?.Value<DateTime?>();
                if (ts.HasValue)
                {
                    _lastScanAt = DateTime.SpecifyKind(ts.Value, DateTimeKind.Utc);
                    var local = _lastScanAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                    LastScanTime = local;
                    LastScanText = $"Ultima varredura: {local}";
                }
            }
            catch { /* state file invalid — ignora */ }
        }

        private void SaveScanState()
        {
            try
            {
                Directory.CreateDirectory(_outputFolderPath);
                var obj = new JObject
                {
                    ["lastScanAtUtc"] = _lastScanAt.ToString("o",
                        System.Globalization.CultureInfo.InvariantCulture)
                };
                File.WriteAllText(StateFilePath, obj.ToString());
            }
            catch { /* nao critico */ }
        }

        private void RefreshPlayersFileState()
        {
            PlayersFileFound = File.Exists(_playersFilePath);
            if (PlayersFileFound)
            {
                try
                {
                    PlayerCount = ReadPlayersFile().Count;
                    PlayersFileStatusText = $"players.txt: Encontrado ({PlayerCount} players)";
                }
                catch
                {
                    PlayerCount = 0;
                    PlayersFileStatusText = "players.txt: Encontrado (erro ao ler)";
                }
            }
            else
            {
                PlayerCount = 0;
                PlayersFileStatusText = $"players.txt: Nao encontrado. Crie em {_playersFilePath} (uma gamertag por linha).";
            }

            UpdateCanStartScan();
        }

        private void UpdateCanStartScan()
        {
            CanStartScan = IsInitialized && PlayersFileFound && !IsScanning;
        }

        partial void OnIsInitializedChanged(bool value) => UpdateCanStartScan();
        partial void OnPlayersFileFoundChanged(bool value) => UpdateCanStartScan();
        partial void OnIsScanningChanged(bool value)
        {
            UpdateCanStartScan();
            ScanProgressVisibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool IsLoggedIn()
        {
            return HomeViewModel.InitComplete && !string.IsNullOrEmpty(HomeViewModel.XAUTH);
        }

        private void OnDailyTimerTick(object? state)
        {
            if (IsScanning) return;
            if (!IsLoggedIn() || !File.Exists(_playersFilePath)) return;
            _ = Task.Run(() => RunScanAsync(_cts.Token));
        }

        private void OnCountdownTick(object? state)
        {
            string countdown;
            if (_lastScanAt == DateTime.MinValue)
            {
                countdown = "--:--:--";
            }
            else
            {
                var remaining = ScanInterval - (DateTime.UtcNow - _lastScanAt);
                if (remaining <= TimeSpan.Zero) remaining = TimeSpan.Zero;
                countdown = remaining.ToString(@"hh\:mm\:ss");
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                NextScanCountdown = countdown;
                NextScanText = $"Proxima varredura: {countdown}";
            });
        }

        private List<string> ReadPlayersFile()
        {
            var list = new List<string>();
            if (!File.Exists(_playersFilePath)) return list;

            foreach (var raw in File.ReadAllLines(_playersFilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;
                list.Add(line);
            }
            return list;
        }

        [RelayCommand]
        public async Task StartScan()
        {
            if (!IsLoggedIn())
            {
                _snackbarService.Show("Nao logado",
                    "Faca login antes de iniciar a varredura.",
                    ControlAppearance.Caution,
                    new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                return;
            }

            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }

            await Task.Run(() => RunScanAsync(_cts.Token));
        }

        [RelayCommand]
        public void StopScan()
        {
            try { _cts.Cancel(); } catch { /* ignored */ }
        }

        [RelayCommand]
        public void OpenOutputFolder()
        {
            try
            {
                Directory.CreateDirectory(_outputFolderPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = _outputFolderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AddLog($"ERRO ao abrir pasta: {ex.Message}");
            }
        }

        [RelayCommand]
        public void OpenPlayersFile()
        {
            try
            {
                if (!File.Exists(_playersFilePath))
                {
                    Directory.CreateDirectory(AppRoot);
                    File.WriteAllText(_playersFilePath,
                        "# Uma gamertag por linha. Linhas iniciando com # sao ignoradas.\r\n");
                    RefreshPlayersFileState();
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = _playersFilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AddLog($"ERRO ao abrir players.txt: {ex.Message}");
            }
        }

        private async Task RunScanAsync(CancellationToken ct)
        {
            if (!await _scanLock.WaitAsync(0))
            {
                AddLog("Varredura ja em andamento, pulando este ciclo.");
                return;
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                IsScanning = true;
                ProgressoCurrent = 0;
                CurrentPlayerName = "";
                StatusMessage = "Iniciando...";
            });
            _hasEverScanned = true;

            try
            {
                if (!IsLoggedIn())
                {
                    AddLog("Abortado: usuario nao esta logado.");
                    return;
                }

                Directory.CreateDirectory(_outputFolderPath);
                string imagesFolder = Path.Combine(_outputFolderPath, "images");
                Directory.CreateDirectory(imagesFolder);
                string profilePictureFolder = Path.Combine(_outputFolderPath, "profilePicture");
                Directory.CreateDirectory(profilePictureFolder);

                var gamertags = ReadPlayersFile();
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    TotalPlayers = gamertags.Count;
                    ProgressoCurrent = 0;
                });

                if (gamertags.Count == 0)
                {
                    AddLog("players.txt esta vazio. Nada a fazer.");
                    return;
                }

                var api = new XboxRestAPI(HomeViewModel.XAUTH);
                int index = 0;

                foreach (var gamertag in gamertags)
                {
                    ct.ThrowIfCancellationRequested();
                    index++;

                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        CurrentPlayerName = gamertag;
                        CurrentPlayerProgressText = $"Varrendo: {gamertag} ({index}/{gamertags.Count})";
                        StatusMessage = "Resolvendo gamertag...";
                    });

                    AddLog($"Iniciando: {gamertag}");

                    try
                    {
                        var profileData = await api.GetGamertagProfileAsync(gamertag);
                        if (profileData == null)
                        {
                            AddLog($"ERRO {gamertag}: perfil nulo.");
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => ProgressoCurrent = index);
                            continue;
                        }

                        var userToken = profileData["profileUsers"]?.FirstOrDefault();
                        if (userToken == null)
                        {
                            AddLog($"ERRO {gamertag}: profileUsers vazio.");
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => ProgressoCurrent = index);
                            continue;
                        }

                        string xuid = userToken["id"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(xuid))
                        {
                            AddLog($"ERRO {gamertag}: XUID nao encontrado.");
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => ProgressoCurrent = index);
                            continue;
                        }

                        string canonicalGamertag = userToken["settings"]
                            ?.FirstOrDefault(s => s["id"]?.ToString() == "Gamertag")
                            ?["value"]?.ToString() ?? gamertag;

                        // Profile picture (GameDisplayPicRaw). Salva 1x em
                        // csv/profilePicture/[xuid].jpg; se ja existe, pula.
                        string? profilePicUrl = userToken["settings"]
                            ?.FirstOrDefault(s => s["id"]?.ToString() == "GameDisplayPicRaw")
                            ?["value"]?.ToString();
                        await DownloadProfilePictureAsync(profilePictureFolder, xuid, profilePicUrl, ct);

                        string playerFolder = Path.Combine(_outputFolderPath, xuid);
                        Directory.CreateDirectory(playerFolder);

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            StatusMessage = $"Buscando jogos de {canonicalGamertag}...");

                        var titlesList = await api.GetGamesListAsync(xuid);
                        var titles = titlesList?.Titles ?? new List<Title>();
                        AddLog($"{canonicalGamertag} ({xuid}): {titles.Count} jogos encontrados");

                        if (titles.Count == 0)
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() => ProgressoCurrent = index);
                            continue;
                        }

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            StatusMessage = $"Buscando tempo de jogo (0/{titles.Count})...");

                        var statsMap = await FetchAllStatsAsync(api, xuid, titles, ct);

                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            StatusMessage = "Salvando CSV...");
                        await SaveGamesCsvAsync(playerFolder, canonicalGamertag, xuid, titles, statsMap);

                        int withImage = titles.Count(t => !string.IsNullOrWhiteSpace(t.DisplayImage));
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                            StatusMessage = $"Baixando imagens... 0/{withImage}");
                        int downloaded = await DownloadImagesAsync(imagesFolder, titles, ct);

                        AddLog($"{canonicalGamertag}: concluido. {titles.Count} jogos, {downloaded} imagens.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERRO {gamertag}: {ex.Message}");
                    }

                    System.Windows.Application.Current?.Dispatcher.Invoke(() => ProgressoCurrent = index);
                }

                _lastScanAt = DateTime.UtcNow;
                SaveScanState();
                var local = _lastScanAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    LastScanTime = local;
                    LastScanText = $"Ultima varredura: {local}";
                    StatusMessage = "Varredura completa.";
                });
                AddLog("Varredura completa.");
            }
            catch (OperationCanceledException)
            {
                AddLog("Varredura cancelada pelo usuario.");
                System.Windows.Application.Current?.Dispatcher.Invoke(() => StatusMessage = "Cancelada.");
            }
            catch (Exception ex)
            {
                AddLog($"ERRO FATAL: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher.Invoke(() => StatusMessage = $"Erro: {ex.Message}");
            }
            finally
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsScanning = false;
                    CurrentPlayerProgressText = "";
                });
                _scanLock.Release();
            }
        }

        // Stats fetching: serial + cache em disco + backoff. O endpoint
        // /userstats e quem mais aciona HTTP 429 (Too Many Requests).
        // Estrategia: cache por jogador valido por 7 dias; chamadas serializadas
        // com 300ms entre cada; se varias falhas seguidas, aumenta o delay.
        private static readonly TimeSpan StatsCacheTtl = TimeSpan.FromDays(7);
        private const int StatsBaseDelayMs = 300;

        private class StatsCacheEntry
        {
            public long Minutes { get; set; }
            public DateTime FetchedAtUtc { get; set; }
        }

        private Dictionary<string, StatsCacheEntry> LoadStatsCache(string path)
        {
            var result = new Dictionary<string, StatsCacheEntry>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return result;
                var obj = JObject.Parse(File.ReadAllText(path));
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value is not JObject entry) continue;
                    var fetched = entry["fetchedAtUtc"]?.Value<DateTime?>();
                    result[prop.Name] = new StatsCacheEntry
                    {
                        Minutes = entry["minutes"]?.Value<long>() ?? 0,
                        FetchedAtUtc = fetched.HasValue
                            ? DateTime.SpecifyKind(fetched.Value, DateTimeKind.Utc)
                            : DateTime.MinValue
                    };
                }
            }
            catch { /* cache corrompido — ignora */ }
            return result;
        }

        private void SaveStatsCache(string path, Dictionary<string, StatsCacheEntry> cache)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var obj = new JObject();
                foreach (var kv in cache)
                {
                    obj[kv.Key] = new JObject
                    {
                        ["minutes"] = kv.Value.Minutes,
                        ["fetchedAtUtc"] = kv.Value.FetchedAtUtc
                            .ToString("o", System.Globalization.CultureInfo.InvariantCulture)
                    };
                }
                File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* nao critico */ }
        }

        private async Task<Dictionary<string, long>> FetchAllStatsAsync(
            XboxRestAPI api, string xuid, List<Title> titles, CancellationToken ct)
        {
            string cachePath = Path.Combine(_outputFolderPath, xuid, "stats_cache.json");
            var cache = LoadStatsCache(cachePath);
            var map = new Dictionary<string, long>();
            var now = DateTime.UtcNow;

            // 1a passada: serve do cache tudo que esta dentro do TTL
            var toFetch = new List<Title>();
            foreach (var title in titles)
            {
                if (string.IsNullOrWhiteSpace(title.TitleId)) continue;
                if (cache.TryGetValue(title.TitleId, out var entry) &&
                    (now - entry.FetchedAtUtc) < StatsCacheTtl)
                {
                    map[title.TitleId] = entry.Minutes;
                }
                else
                {
                    toFetch.Add(title);
                }
            }

            int total = titles.Count(t => !string.IsNullOrWhiteSpace(t.TitleId));
            int cached = map.Count;
            AddLog($"  stats: {cached}/{total} do cache, {toFetch.Count} para buscar");

            // 2a passada: busca o que falta SERIALMENTE com throttle adaptativo
            int done = cached;
            int consecutiveFailures = 0;

            foreach (var title in toFetch)
            {
                ct.ThrowIfCancellationRequested();

                long minutes = 0;
                bool got = false;
                try
                {
                    var stats = await api.GetGameStatsAsync(xuid, title.TitleId!);
                    var raw = stats?.StatListsCollection?.FirstOrDefault()
                        ?.Stats?.FirstOrDefault()?.Value;
                    if (!string.IsNullOrEmpty(raw) && long.TryParse(raw, out var parsed))
                    {
                        minutes = parsed;
                    }
                    got = stats != null && stats.StatListsCollection != null;
                }
                catch
                {
                    got = false;
                }

                if (got) consecutiveFailures = 0;
                else consecutiveFailures++;

                map[title.TitleId!] = minutes;
                cache[title.TitleId!] = new StatsCacheEntry
                {
                    Minutes = minutes,
                    FetchedAtUtc = DateTime.UtcNow
                };

                done++;
                if (done % 25 == 0 || done == total)
                {
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        StatusMessage = $"Buscando tempo de jogo ({done}/{total})...");
                }

                // Backoff adaptativo: 6+ falhas seguidas = 5s, 3+ = 1.5s, normal = 300ms
                int delayMs = consecutiveFailures >= 6 ? 5000
                            : consecutiveFailures >= 3 ? 1500
                            : StatsBaseDelayMs;
                try { await Task.Delay(delayMs, ct); }
                catch (OperationCanceledException)
                {
                    SaveStatsCache(cachePath, cache);
                    throw;
                }

                // Salva cache a cada 50 buscas pra nao perder progresso se cair
                if (done % 50 == 0) SaveStatsCache(cachePath, cache);
            }

            SaveStatsCache(cachePath, cache);
            return map;
        }

        private static readonly string[] CsvHeaders = new[]
        {
            // Contexto da varredura
            "ScanDate", "Gamertag", "Xuid",
            // Identificadores
            "TitleId", "ModernTitleId", "Pfn", "BingId", "ServiceConfigId", "WindowsPhoneProductId",
            "ProductId", "AlternateProductId",
            // Identidade / classificacao
            "Name", "VuiDisplayName", "Type", "MediaItemType", "Devices", "IsBundle",
            "IsStreamable", "XboxLiveTier", "XboxLiveGoldRequired",
            // Detail textual
            "DeveloperName", "PublisherName", "ReleaseDate", "MinAge",
            "ShortDescription", "Description",
            "Genres", "Attributes", "Capabilities", "Availabilities",
            // Imagem
            "DisplayImage", "DisplayImageHighQuality", "LocalImageFile", "ImageCount",
            // Conquistas
            "CurrentAchievements", "TotalAchievements", "CurrentGamerscore", "TotalGamerscore",
            "ProgressPercentage", "RemainingGamerscore", "RemainingAchievements",
            "IsCompleted", "CompletionRatio",
            "AchievementSourceVersion", "HasAchievements",
            // Game Pass
            "IsGamePass", "HasGamePassDetail",
            // Historico
            "LastTimePlayed", "DaysSinceLastPlay", "Visible", "CanHide",
            // Stats / Tempo
            "StatsSourceVersion", "TimePlayedMinutes", "TimePlayedHours", "TimePlayedFormatted",
            // Estruturas restantes (JSON cru, para nao perder nada)
            "ContentBoards", "Images", "TitleRecord", "AlternateTitleIds", "FriendsWhoPlayed",
            // Catch-all para qualquer campo novo que a API passe a retornar
            "TitleExtras", "DetailExtras"
        };

        private async Task SaveGamesCsvAsync(string playerFolder, string gamertag, string xuid,
            List<Title> titles, Dictionary<string, long> statsMap)
        {
            Directory.CreateDirectory(playerFolder);
            string csvPath = Path.Combine(playerFolder, "games.csv");

            string scanDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture);

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", CsvHeaders.Select(CsvCell)));

            var inv = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var title in titles)
            {
                string titleId = title.TitleId ?? "";
                long minutes = statsMap.TryGetValue(titleId, out var m) ? m : 0;
                double hours = Math.Round(minutes / 60.0, 2);
                var ts = TimeSpan.FromMinutes(minutes);
                string formatted = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";

                string displayImage = title.DisplayImage ?? "";
                string displayImageHq = string.IsNullOrEmpty(displayImage)
                    ? ""
                    : GetHighQualityImageUrl(displayImage);
                string localImageFile = string.IsNullOrEmpty(titleId) ? "" : $"images/{titleId}.jpg";

                // Detail extension data (description, developerName, etc.)
                var detailExt = title.Detail?.ExtensionData;
                string developerName = ExtString(detailExt, "developerName");
                string publisherName = ExtString(detailExt, "publisherName");
                string releaseDate = ExtDate(detailExt, "releaseDate");
                string minAge = ExtNumber(detailExt, "minAge", "minimumAge");
                string shortDescription = ExtString(detailExt, "shortDescription");
                string description = ExtString(detailExt, "description");
                string vuiDisplayName = ExtString(detailExt, "vuiDisplayName");
                string xboxLiveGoldRequired = ExtBool(detailExt, "xboxLiveGoldRequired");
                string attributes = ExtJoinList(detailExt, "attributes",
                    el => el is JObject o ? (o["name"]?.ToString() ?? o.ToString(Newtonsoft.Json.Formatting.None))
                                          : el?.ToString() ?? "");
                string capabilities = ExtJoinStrings(detailExt, "capabilities");
                string availabilities = ExtJsonCompact(detailExt, "availabilities");

                // Title extension data (isStreamable, productId, etc.)
                var titleExt = title.ExtensionData;
                string isStreamable = ExtBool(titleExt, "isStreamable");
                string productId = ExtString(titleExt, "productId");
                string alternateProductId = ExtString(titleExt, "alternateProductId");
                int imageCount = (title.Images is JArray jaImg) ? jaImg.Count : 0;

                // Derivados de Achievement
                int curAch = title.Achievement?.CurrentAchievements ?? 0;
                int totAch = title.Achievement?.TotalAchievements ?? 0;
                int curGs = title.Achievement?.CurrentGamerscore ?? 0;
                int totGs = title.Achievement?.TotalGamerscore ?? 0;
                string remainingGs = title.Achievement == null ? "" : (totGs - curGs).ToString(inv);
                string remainingAch = title.Achievement == null ? "" : (totAch - curAch).ToString(inv);
                string isCompleted = title.Achievement == null
                    ? ""
                    : (totAch > 0 && curAch >= totAch).ToString();
                string completionRatio = (title.Achievement == null || totAch == 0)
                    ? ""
                    : Math.Round((double)curAch / totAch * 100.0, 2).ToString(inv);

                // Dias desde a ultima vez que jogou
                string daysSince = "";
                if (title.TitleHistory != null && title.TitleHistory.LastTimePlayed != default)
                {
                    var d = (DateTime.UtcNow - title.TitleHistory.LastTimePlayed.ToUniversalTime()).TotalDays;
                    if (d >= 0) daysSince = Math.Round(d, 1).ToString(inv);
                }

                var cells = new[]
                {
                    // Contexto
                    CsvCell(scanDate),
                    CsvCell(gamertag),
                    CsvCell(xuid),
                    // Identificadores
                    CsvCell(titleId),
                    CsvCell(title.ModernTitleId),
                    CsvCell(title.Pfn),
                    CsvCell(title.BingId),
                    CsvCell(title.ServiceConfigId),
                    CsvCell(title.WindowsPhoneProductId),
                    CsvCell(productId),
                    CsvCell(alternateProductId),
                    // Identidade
                    CsvCell(title.Name),
                    CsvCell(vuiDisplayName),
                    CsvCell(title.Type),
                    CsvCell(title.MediaItemType),
                    CsvCell(title.Devices == null ? "" : string.Join("|", title.Devices)),
                    CsvCell(title.IsBundle.ToString()),
                    CsvCell(isStreamable),
                    CsvCell(title.XboxLiveTier),
                    CsvCell(xboxLiveGoldRequired),
                    // Detail textual
                    CsvCell(developerName),
                    CsvCell(publisherName),
                    CsvCell(releaseDate),
                    CsvCell(minAge),
                    CsvCell(shortDescription),
                    CsvCell(description),
                    CsvCell(title.Detail?.Genres != null ? string.Join("|", title.Detail.Genres) : ""),
                    CsvCell(attributes),
                    CsvCell(capabilities),
                    CsvCell(availabilities),
                    // Imagem
                    CsvCell(displayImage),
                    CsvCell(displayImageHq),
                    CsvCell(localImageFile),
                    CsvCell(imageCount.ToString(inv)),
                    // Conquistas
                    CsvCell(title.Achievement == null ? "" : curAch.ToString(inv)),
                    CsvCell(title.Achievement == null ? "" : totAch.ToString(inv)),
                    CsvCell(title.Achievement == null ? "" : curGs.ToString(inv)),
                    CsvCell(title.Achievement == null ? "" : totGs.ToString(inv)),
                    CsvCell(title.Achievement?.ProgressPercentage.ToString(inv) ?? ""),
                    CsvCell(remainingGs),
                    CsvCell(remainingAch),
                    CsvCell(isCompleted),
                    CsvCell(completionRatio),
                    CsvCell(title.Achievement?.SourceVersion.ToString(inv) ?? ""),
                    CsvCell((totAch > 0).ToString()),
                    // GamePass
                    CsvCell(ExtractIsGamePass(title.GamePass)),
                    CsvCell(title.Detail?.HasGamePass.ToString() ?? ""),
                    // Historico
                    CsvCell(title.TitleHistory?.LastTimePlayed
                        .ToString("yyyy-MM-ddTHH:mm:ssZ", inv) ?? ""),
                    CsvCell(daysSince),
                    CsvCell(title.TitleHistory?.Visible.ToString() ?? ""),
                    CsvCell(title.TitleHistory?.CanHide.ToString() ?? ""),
                    // Stats / tempo
                    CsvCell(ExtractStatsSourceVersion(title.Stats)),
                    CsvCell(minutes.ToString(inv)),
                    CsvCell(hours.ToString(inv)),
                    CsvCell(formatted),
                    // JSON cru das estruturas opacas
                    CsvCell(SerializeOpaque(title.ContentBoards)),
                    CsvCell(SerializeOpaque(title.Images)),
                    CsvCell(SerializeOpaque(title.TitleRecord)),
                    CsvCell(SerializeOpaque(title.AlternateTitleIds)),
                    CsvCell(SerializeOpaque(title.FriendsWhoPlayed)),
                    // Catch-all (chaves nao reconhecidas)
                    CsvCell(SerializeExtension(titleExt, KnownTitleExtKeys)),
                    CsvCell(SerializeExtension(detailExt, KnownDetailExtKeys))
                };

                sb.AppendLine(string.Join(",", cells));
            }

            await File.WriteAllTextAsync(csvPath, sb.ToString(), new UTF8Encoding(true));
        }

        // Chaves de Detail.ExtensionData ja capturadas em colunas dedicadas; tudo fora
        // dessa lista cai em DetailExtras como JSON.
        private static readonly HashSet<string> KnownDetailExtKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "developerName", "publisherName", "releaseDate", "minAge", "minimumAge",
            "shortDescription", "description", "vuiDisplayName", "xboxLiveGoldRequired",
            "attributes", "capabilities", "availabilities"
        };

        // Chaves de Title.ExtensionData ja capturadas; tudo fora cai em TitleExtras.
        private static readonly HashSet<string> KnownTitleExtKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "isStreamable", "productId", "alternateProductId"
        };

        private static JToken? ExtGet(IDictionary<string, JToken>? ext, params string[] names)
        {
            if (ext == null) return null;
            foreach (var name in names)
            {
                foreach (var kv in ext)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return null;
        }

        private static string ExtString(IDictionary<string, JToken>? ext, params string[] names)
        {
            var t = ExtGet(ext, names);
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }

        private static string ExtNumber(IDictionary<string, JToken>? ext, params string[] names)
        {
            var t = ExtGet(ext, names);
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }

        private static string ExtBool(IDictionary<string, JToken>? ext, params string[] names)
        {
            var t = ExtGet(ext, names);
            if (t == null || t.Type == JTokenType.Null) return "";
            return t.ToString();
        }

        private static string ExtDate(IDictionary<string, JToken>? ext, params string[] names)
        {
            var t = ExtGet(ext, names);
            if (t == null || t.Type == JTokenType.Null) return "";
            if (t.Type == JTokenType.Date)
            {
                var dt = t.Value<DateTime>();
                return dt.ToString("yyyy-MM-ddTHH:mm:ssZ",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return t.ToString();
        }

        private static string ExtJoinStrings(IDictionary<string, JToken>? ext, string name)
        {
            var t = ExtGet(ext, name);
            if (t is not JArray arr) return "";
            var parts = arr
                .Where(x => x.Type != JTokenType.Null)
                .Select(x => x.ToString())
                .ToArray();
            return string.Join("|", parts);
        }

        private static string ExtJoinList(IDictionary<string, JToken>? ext, string name, Func<JToken, string> project)
        {
            var t = ExtGet(ext, name);
            if (t is not JArray arr) return "";
            return string.Join("|", arr.Select(project).Where(s => !string.IsNullOrEmpty(s)));
        }

        private static string ExtJsonCompact(IDictionary<string, JToken>? ext, string name)
        {
            var t = ExtGet(ext, name);
            if (t == null || t.Type == JTokenType.Null) return "";
            if (t is JArray arr && arr.Count == 0) return "";
            if (t is JObject obj && !obj.HasValues) return "";
            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string SerializeExtension(IDictionary<string, JToken>? ext, HashSet<string> known)
        {
            if (ext == null || ext.Count == 0) return "";
            var leftover = new JObject();
            foreach (var kv in ext)
            {
                if (known.Contains(kv.Key)) continue;
                if (kv.Value == null || kv.Value.Type == JTokenType.Null) continue;
                leftover[kv.Key] = kv.Value;
            }
            return leftover.HasValues ? leftover.ToString(Newtonsoft.Json.Formatting.None) : "";
        }

        private static string CsvCell(string? value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string ExtractStatsSourceVersion(object? stats)
        {
            if (stats is null) return "";
            if (stats is JObject jobj)
            {
                var token = jobj["sourceVersion"] ?? jobj["SourceVersion"];
                if (token != null) return token.ToString();
            }
            return "";
        }

        private static string SerializeOpaque(object? value)
        {
            if (value is null) return "";
            try
            {
                if (value is JToken jt)
                {
                    if (jt.Type == JTokenType.Null) return "";
                    if (jt is JArray arr && arr.Count == 0) return "";
                    if (jt is JObject obj && !obj.HasValues) return "";
                    return jt.ToString(Newtonsoft.Json.Formatting.None);
                }
                return Newtonsoft.Json.JsonConvert.SerializeObject(value);
            }
            catch
            {
                return "";
            }
        }

        private static readonly HashSet<string> SizeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "w", "h", "width", "height", "mode", "q", "quality", "format", "background"
        };

        private static string GetHighQualityImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            int qIndex = url.IndexOf('?');
            if (qIndex < 0) return url;

            string baseUrl = url.Substring(0, qIndex);
            string query = url.Substring(qIndex + 1);

            var kept = new List<string>();
            foreach (var part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = eq >= 0 ? part.Substring(0, eq) : part;
                if (SizeQueryKeys.Contains(key)) continue;
                kept.Add(part);
            }

            return kept.Count == 0 ? baseUrl : baseUrl + "?" + string.Join("&", kept);
        }

        private static string ExtractIsGamePass(object? gamePass)
        {
            if (gamePass is null) return "";
            if (gamePass is JObject jobj)
            {
                var token = jobj["isGamePass"] ?? jobj["IsGamePass"];
                if (token != null) return token.ToString();
            }
            return "";
        }

        private async Task<int> DownloadImagesAsync(string imagesFolder, List<Title> titles, CancellationToken ct)
        {
            Directory.CreateDirectory(imagesFolder);

            var candidates = titles
                .Where(t => !string.IsNullOrWhiteSpace(t.DisplayImage) && !string.IsNullOrWhiteSpace(t.TitleId))
                .ToList();

            int totalWithImage = candidates.Count;
            int downloaded = 0;
            int processed = 0;
            var semaphore = new SemaphoreSlim(5);

            var tasks = candidates.Select(async title =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    string localPath = Path.Combine(imagesFolder, $"{title.TitleId}.jpg");

                    if (File.Exists(localPath))
                    {
                        Interlocked.Increment(ref processed);
                        return;
                    }

                    try
                    {
                        string url = GetHighQualityImageUrl(title.DisplayImage!);
                        using var resp = await _imageHttpClient.GetAsync(url, ct);
                        resp.EnsureSuccessStatusCode();
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        await File.WriteAllBytesAsync(localPath, bytes, ct);
                        Interlocked.Increment(ref downloaded);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AddLog($"  imagem falhou {title.TitleId}: {ex.Message}");
                    }
                    finally
                    {
                        int p = Interlocked.Increment(ref processed);
                        if (p % 10 == 0 || p == totalWithImage)
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                StatusMessage = $"Baixando imagens... {p}/{totalWithImage}");
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { throw; }
            catch { /* per-image failures already logged */ }

            return downloaded;
        }

        private async Task DownloadProfilePictureAsync(string profileFolder, string xuid,
            string? url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(xuid) || string.IsNullOrWhiteSpace(url))
                return;

            string localPath = Path.Combine(profileFolder, $"{xuid}.jpg");
            if (File.Exists(localPath))
                return; // ja temos

            // GameDisplayPicRaw vem com "&mode=Padding" por padrao; tira pra pegar
            // a versao original (mesma logica do MiscViewModel.GamertagImage).
            string fetchUrl = GetHighQualityImageUrl(url.Replace("&mode=Padding", ""));

            try
            {
                using var resp = await _imageHttpClient.GetAsync(fetchUrl, ct);
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                await File.WriteAllBytesAsync(localPath, bytes, ct);
                AddLog($"  profile picture salva: {xuid}.jpg");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"  profile picture falhou para {xuid}: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LogLines.Add(line);
                while (LogLines.Count > 500) LogLines.RemoveAt(0);
            });
        }
    }
}
