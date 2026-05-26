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

        private static readonly string DocsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU");
        private readonly string _playersFilePath = Path.Combine(DocsRoot, "players.txt");
        private readonly string _outputFolderPath = Path.Combine(DocsRoot, "csv");

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

            if (IsInitialized && PlayersFileFound && !_hasEverScanned && IsLoggedIn())
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

            _dailyTimer = new System.Threading.Timer(OnDailyTimerTick, null,
                ScanInterval, ScanInterval);

            _countdownTimer = new System.Threading.Timer(OnCountdownTick, null,
                TimeSpan.Zero, TimeSpan.FromSeconds(1));

            UpdateCanStartScan();
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
                    Directory.CreateDirectory(DocsRoot);
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
                        await SaveGamesCsvAsync(playerFolder, titles, statsMap);

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

        private async Task<Dictionary<string, long>> FetchAllStatsAsync(
            XboxRestAPI api, string xuid, List<Title> titles, CancellationToken ct)
        {
            var map = new Dictionary<string, long>();
            var mapLock = new object();
            var semaphore = new SemaphoreSlim(3);
            int done = 0;

            var tasks = titles
                .Where(t => !string.IsNullOrWhiteSpace(t.TitleId))
                .Select(async title =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        long minutes = 0;
                        try
                        {
                            var stats = await api.GetGameStatsAsync(xuid, title.TitleId!);
                            var raw = stats?.StatListsCollection?.FirstOrDefault()
                                ?.Stats?.FirstOrDefault()?.Value;
                            if (!string.IsNullOrEmpty(raw) && long.TryParse(raw, out var parsed))
                            {
                                minutes = parsed;
                            }
                        }
                        catch
                        {
                            minutes = 0;
                        }

                        lock (mapLock) map[title.TitleId!] = minutes;

                        int current = Interlocked.Increment(ref done);
                        if (current % 25 == 0 || current == titles.Count)
                        {
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                                StatusMessage = $"Buscando tempo de jogo ({current}/{titles.Count})...");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                })
                .ToList();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { throw; }
            catch { /* per-title failures already swallowed */ }

            return map;
        }

        private async Task SaveGamesCsvAsync(string playerFolder, List<Title> titles,
            Dictionary<string, long> statsMap)
        {
            Directory.CreateDirectory(playerFolder);
            string csvPath = Path.Combine(playerFolder, "games.csv");

            var sb = new StringBuilder();
            sb.AppendLine("\"TitleId\",\"Name\",\"Type\",\"Devices\",\"CurrentAchievements\",\"TotalAchievements\",\"CurrentGamerscore\",\"TotalGamerscore\",\"ProgressPercentage\",\"IsGamePass\",\"TimePlayed\",\"DisplayImage\",\"ServiceConfigId\",\"Pfn\",\"BingId\"");

            foreach (var title in titles)
            {
                string titleId = title.TitleId ?? "";
                string name = (title.Name ?? "").Replace("\"", "\"\"");
                string type = title.Type ?? "";
                string devices = title.Devices != null ? string.Join("|", title.Devices) : "";
                string curAch = title.Achievement?.CurrentAchievements.ToString() ?? "";
                string totAch = title.Achievement?.TotalAchievements.ToString() ?? "";
                string curGs = title.Achievement?.CurrentGamerscore.ToString() ?? "";
                string totGs = title.Achievement?.TotalGamerscore.ToString() ?? "";
                string progress = title.Achievement?.ProgressPercentage.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                string isGp = ExtractIsGamePass(title.GamePass);
                string timePlayed = statsMap.TryGetValue(titleId, out var mins) ? mins.ToString() : "0";
                string displayImage = (title.DisplayImage ?? "").Replace("\"", "\"\"");
                string scid = title.ServiceConfigId ?? "";
                string pfn = (title.Pfn ?? "").Replace("\"", "\"\"");
                string bingId = title.BingId ?? "";

                sb.AppendLine($"\"{titleId}\",\"{name}\",\"{type}\",\"{devices}\",\"{curAch}\",\"{totAch}\",\"{curGs}\",\"{totGs}\",\"{progress}\",\"{isGp}\",\"{timePlayed}\",\"{displayImage}\",\"{scid}\",\"{pfn}\",\"{bingId}\"");
            }

            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);
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
