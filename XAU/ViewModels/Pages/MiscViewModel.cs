using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.DirectoryServices;
using System.IO;
using System.Text;
using System.Windows.Input;
using Wpf.Ui.Common;
using Wpf.Ui.Contracts;
using Wpf.Ui.Controls;
using Wpf.Ui.Services;


namespace XAU.ViewModels.Pages
{
    public partial class MiscViewModel : ObservableObject, INavigationAware
    {
        private readonly IContentDialogService _contentDialogService;
        private readonly ISnackbarService _snackbarService;
        private TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        private Lazy<XboxRestAPI> _xboxRestAPI = new Lazy<XboxRestAPI>(() => new XboxRestAPI(() => HomeViewModel.XAUTH));

        // Spoof/presence calls need the presence-capable token (SpoofXAUTH); reads keep using XAUTH.
        // Used for the one-shot calls (stop); the loops below use their own cached instance.
        private static XboxRestAPI GetSpoofApi() => new XboxRestAPI(() => XboxRestAPI.GetSpoofAuth());

        // One cached client per loop. The token is resolved per request, so the instance stays valid
        // across token renewals, and a 10h session no longer churns a new HttpClient every cycle.
        // Separate instances because both loops can run at once and each mutates its own headers.
        private static readonly Lazy<XboxRestAPI> _singleSpoofApi = new(() => new XboxRestAPI(() => XboxRestAPI.GetSpoofAuth()));
        private static readonly Lazy<XboxRestAPI> _multiSpoofApi = new(() => new XboxRestAPI(() => XboxRestAPI.GetSpoofAuth()));

        // Presence stays valid for 600s (see the heartbeat body), so refreshing every 150s keeps every
        // title continuously active with margin for a missed cycle; failures retry sooner.
        private static readonly TimeSpan SpoofRefreshInterval = TimeSpan.FromSeconds(150);
        private static readonly TimeSpan SpoofRetryInterval = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan LiveDeltaInterval = TimeSpan.FromMinutes(5);

        // Sleeps in 1s slices, ticking the UI, and bails out as soon as the session is stopped.
        // Returns true when the loop should stop.
        private static async Task<bool> WaitWhileSpoofingAsync(TimeSpan interval, Func<bool> stopRequested, Action tick)
        {
            var deadline = DateTime.UtcNow + interval;
            while (DateTime.UtcNow < deadline)
            {
                if (stopRequested()) return true;
                try { System.Windows.Application.Current?.Dispatcher.Invoke(tick); } catch { }
                await Task.Delay(1000);
            }
            return stopRequested();
        }

        private static string DescribeSpoofError(SpoofResult result)
        {
            var detail = result.Error ?? "Unknown error";
            if (detail.Contains("403") || detail.Contains("401"))
                detail = "Xbox rejected the spoof token. Open the Xbox app and wait until Home shows 'Attached' (green). Retrying automatically.";
            if (detail.Length > 180)
                detail = detail[..180] + "...";
            return detail;
        }

        // A failed cycle no longer ends the session: warn on the first failure and then only now and
        // then, while the loop keeps retrying. An expired token or a network drop recovers on its own.
        private void NotifySpoofProblem(string title, SpoofResult result, int attempt)
        {
            if (attempt != 1 && attempt % 20 != 0)
                return;

            var detail = DescribeSpoofError(result);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                _snackbarService.Show(title, detail,
                    ControlAppearance.Caution,
                    new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5)));
        }



        public MiscViewModel(ISnackbarService snackbarService)
        {
            _snackbarService = snackbarService;
            _contentDialogService = new ContentDialogService();
        }

        public void OnNavigatedTo()
        {
            if (!IsInitialized && HomeViewModel.InitComplete)
                InitializeViewModel();
        }

        public void OnNavigatedFrom()
        {
        }

        private void InitializeViewModel()
        {
            IsInitialized = true;
        }

        #region Spoofer

        [ObservableProperty] private string _gameName = "Name: ";
        [ObservableProperty] private string _gameTitleID = "Title ID: ";
        [ObservableProperty] private string _gamePFN = "PFN: ";
        [ObservableProperty] private string _gameType = "Type: ";
        [ObservableProperty] private string _gameGamepass = "Gamepass: ";
        [ObservableProperty] private string _gameDevices = "Devices: ";
        [ObservableProperty] private string _gameGamerscore = "Gamerscore: ?/?";
        [ObservableProperty] private string? _gameImage = "pack://application:,,,/Assets/cirno.png";
        [ObservableProperty] private string _gameTime = "Time Played: ";
        [ObservableProperty] private bool _isInitialized = false;
        [ObservableProperty] private string _currentSpoofingID = "";
        [ObservableProperty] private string _newSpoofingID = "";
        [ObservableProperty] private string _spoofingText = "Spoofing Not Started";
        [ObservableProperty] private string _spoofingButtonText = "Start Spoofing";
        private bool SpoofingUpdate = false;
        private bool CurrentlySpoofing = false;
        private GameTitle GameInfoResponse;
        private GameStatsResponse GameStatsResponse;

        [RelayCommand]
        public async Task SpooferButtonClicked()
        {
            if (CurrentlySpoofing)
            {
                SpoofingUpdate = true;
                _spoofSession++;
                CurrentlySpoofing = false;
                SpoofingText = "Spoofing Not Started";
                SpoofingButtonText = "Start Spoofing";
                //reset game info
                GameName = "Name: ";
                GameTitleID = "Title ID: ";
                GamePFN = "PFN: ";
                GameType = "Type: ";
                GameGamepass = "Gamepass: ";
                GameDevices = "Devices: ";
                GameGamerscore = "Gamerscore: ?/?";
                GameImage = "pack://application:,,,/Assets/cirno.png";
                GameTime = "Time Played: ";
                HomeViewModel.SpoofingStatus = 0;
                await GetSpoofApi().StopHeartbeatAsync(HomeViewModel.XUIDOnly);
                return;
            }
            HomeViewModel.SpoofedTitleID = NewSpoofingID;

            if (HomeViewModel.SpoofingStatus == 2)
            {
                HomeViewModel.SpoofingStatus = 1;
                AchievementsViewModel.SpoofingUpdate = true;
            }
            HomeViewModel.SpoofingStatus = 1;
            SpoofGame();
        }

        public async void SpoofGame()
        {
            CurrentSpoofingID = NewSpoofingID;
            GameInfoResponse = await _xboxRestAPI.Value.GetGameTitleAsync(HomeViewModel.XUIDOnly, NewSpoofingID);
            GameStatsResponse = await _xboxRestAPI.Value.GetGameStatsAsync(HomeViewModel.XUIDOnly, NewSpoofingID);

            if (GameInfoResponse == null || GameStatsResponse == null || !GameInfoResponse.Titles.Any())
            {
                _snackbarService.Show("Error: Unable to acquire game info or stats",
                    $"The game info was invalid.",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            try
            {
                GameName = "Name: " + GameInfoResponse.Titles[0].Name;
                GameImage = !string.IsNullOrEmpty(GameInfoResponse.Titles[0].DisplayImage.ToString()) ? GameInfoResponse.Titles[0].DisplayImage.ToString() : "pack://application:,,,/Assets/cirno.png";
                GameTitleID = "Title ID: " + GameInfoResponse.Titles[0].TitleId;
                GamePFN = "PFN: " + GameInfoResponse.Titles[0].Pfn;
                GameType = "Type: " + GameInfoResponse.Titles[0].Type;
                GameGamepass = "Gamepass: " + GameInfoResponse.Titles[0].GamePass?.IsGamePass;
                GameDevices = "Devices: ";
                foreach (var device in GameInfoResponse.Titles[0].Devices)
                {
                    GameDevices += device.ToString() + ", ";
                }

                GameDevices = GameDevices.Remove(GameDevices.Length - 2);
                GameGamerscore = "Gamerscore: " + GameInfoResponse.Titles[0].Achievement?.CurrentGamerscore.ToString() +
                                 "/" + GameInfoResponse.Titles[0].Achievement?.TotalGamerscore.ToString();
                try
                {
                    var timePlayed = TimeSpan.FromMinutes(Convert.ToDouble(GameStatsResponse.StatListsCollection[0].Stats[0].Value));
                    var formattedTime = $"{timePlayed.Days} Days, {timePlayed.Hours} Hours and {timePlayed.Minutes} minutes";
                    GameTime = "Time Played: " + formattedTime;
                }
                catch
                {
                    GameTime = "Time Played: Unknown";
                }

            }
            catch
            {
                GameName = "Name: ";
                _snackbarService.Show("Error: Invalid TitleID",
                    $"The TitleID entered is invalid or does not return information from the API",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            SpoofingUpdate = true;
            CurrentlySpoofing = true;
            SpoofingButtonText = "Stop Spoofing";
            SpoofingText = $"Spoofing {GameInfoResponse.Titles[0].Name}";
            var session = ++_spoofSession;
            await Task.Run(() => Spoofing(session));

        }

        // Identifies the running session. Bumped on every start and stop so a loop can tell it has
        // been superseded: the SpoofingUpdate flag alone isn't enough now that the loop clears it
        // after its first send -- a Stop pressed while that send was in flight would be swallowed
        // and the loop would keep spoofing forever.
        private int _spoofSession = 0;

        // Keeps one title marked as playing until the user stops it. The loop never ends on its own:
        // a rejected token (it ages out after a few hours) or a network drop is retried -- previously
        // any single failure broke out and left the user having to press Start again.
        // TODO: this code seems like it's duplicated in AchievementsViewModel.cs too.
        public async Task Spoofing(int session)
        {
            var stopwatch = Stopwatch.StartNew();
            var titleName = GameInfoResponse?.Titles?.FirstOrDefault()?.Name ?? CurrentSpoofingID;
            var titleId = CurrentSpoofingID;
            var failures = 0;
            bool Stopped() => SpoofingUpdate || session != _spoofSession;

            // The first send also releases any previous loop (SpoofGame set the flag before starting).
            var result = await SpoofSender.SendAsync(_singleSpoofApi.Value, HomeViewModel.XUIDOnly, titleId);
            if (session != _spoofSession) return;
            SpoofingUpdate = false;

            while (!Stopped())
            {
                if (result.Success)
                {
                    failures = 0;
                }
                else
                {
                    failures++;
                    NotifySpoofProblem("Spoofing interrupted", result, failures);
                }

                var healthy = result.Success;
                var attempt = failures;
                if (await WaitWhileSpoofingAsync(
                        healthy ? SpoofRefreshInterval : SpoofRetryInterval,
                        Stopped,
                        () => SpoofingText = healthy
                            ? $"Spoofing {titleName} For: {stopwatch.Elapsed:hh\\:mm\\:ss}"
                            : $"Reconnecting {titleName} (attempt {attempt}) - {stopwatch.Elapsed:hh\\:mm\\:ss}"))
                    break;

                if (!HomeViewModel.IsSignedIn)
                {
                    result = SpoofResult.Fail("Waiting for the Xbox login to come back.");
                    continue;
                }

                result = await SpoofSender.SendAsync(_singleSpoofApi.Value, HomeViewModel.XUIDOnly, titleId);
            }
        }

        #endregion

        #region MultiSpoofer

        [ObservableProperty] private string _multiSpoofingIDs = "";
        [ObservableProperty] private string _multiSpoofingButtonText = "Start Multi-Spoof";
        [ObservableProperty] private string _multiSpoofingStatusText = "Multi-Spoofing Not Started";
        [ObservableProperty] private ObservableCollection<MultiSpoofGameItem> _multiSpoofGames = new();
        private bool _multiCurrentlySpoofing = false;
        private bool _multiSpoofingUpdate = false;
        // Same role as _spoofSession: lets a superseded/stopped multi-spoof loop end itself.
        private int _multiSpoofSession = 0;
        private Lazy<XboxRestAPI> _multiXboxRestAPI = new Lazy<XboxRestAPI>(() => new XboxRestAPI(() => HomeViewModel.XAUTH));

        // --- Multi-Spoof persistencia/historico (Documents\XAU) ---
        private readonly object _multiStateLock = new();
        private DateTime _multiSessionStartedAtUtc = DateTime.MinValue;
        // titleId -> (name, minutos jogados no inicio da sessao)
        private readonly Dictionary<string, (string Name, int Minutes)> _multiSessionStartMinutes = new();
        private bool _multiAutoResumeTried = false;

        private static string XAUFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU");
        private static string MultiSpoofStatePath => Path.Combine(XAUFolder, "multispoof_state.json");
        private static string MultiSpoofHistoryPath => Path.Combine(XAUFolder, "multispoof_history.json");

        [RelayCommand]
        public async Task MultiSpooferButtonClicked()
        {
            if (_multiCurrentlySpoofing)
            {
                _multiSpoofingUpdate = true;
                _multiSpoofSession++;
                _multiCurrentlySpoofing = false;
                HomeViewModel.MultiSpoofingActive = false;
                MultiSpoofingButtonText = "Start Multi-Spoof";
                MultiSpoofingStatusText = "Multi-Spoofing Not Started";
                await RecordMultiSpoofSessionEndAsync();
                SaveMultiSpoofState(false, new List<string>());
                System.Windows.Application.Current.Dispatcher.Invoke(() => MultiSpoofGames.Clear());
                lock (_multiStateLock) _multiSessionStartMinutes.Clear();
                await GetSpoofApi().StopHeartbeatAsync(HomeViewModel.XUIDOnly);
                return;
            }

            var ids = (MultiSpoofingIDs ?? string.Empty)
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                _snackbarService.Show("Error", "Please enter one or more Title IDs separated by commas.",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() => MultiSpoofGames.Clear());
            MultiSpoofingStatusText = $"Loading {ids.Count} title(s)...";

            await LoadMultiSpoofGamesAsync(ids);

            if (MultiSpoofGames.Count == 0)
            {
                MultiSpoofingStatusText = "Multi-Spoofing Not Started";
                return;
            }

            var validIds = MultiSpoofGames.Select(g => g.TitleId).ToList();
            _multiCurrentlySpoofing = true;
            _multiSpoofingUpdate = false;
            HomeViewModel.MultiSpoofingActive = true;
            _multiSessionStartedAtUtc = DateTime.UtcNow;
            MultiSpoofingButtonText = "Stop Multi-Spoof";
            MultiSpoofingStatusText = $"Multi-Spoofing {validIds.Count} title(s)";

            SaveMultiSpoofState(true, validIds);
            ApplyLastSessionDeltas(); // mostra o delta da sessao anterior em cada jogo

            var session = ++_multiSpoofSession;
            _ = Task.Run(() => MultiSpoofingLoop(validIds, session));
        }

        private async Task LoadMultiSpoofGamesAsync(List<string> titleIds)
        {
            var failedIds = new List<string>();
            var lockObj = new object();

            var tasks = titleIds.Select(async id =>
            {
                try
                {
                    var gameInfo = await _multiXboxRestAPI.Value.GetGameTitleAsync(HomeViewModel.XUIDOnly, id);
                    var gameStats = await _multiXboxRestAPI.Value.GetGameStatsAsync(HomeViewModel.XUIDOnly, id);

                    if (gameInfo == null || gameStats == null || gameInfo.Titles == null || !gameInfo.Titles.Any())
                    {
                        lock (lockObj) failedIds.Add(id);
                        return;
                    }

                    var title = gameInfo.Titles[0];

                    string gamerscore = $"{title.Achievement?.CurrentGamerscore ?? 0}/{title.Achievement?.TotalGamerscore ?? 0}";

                    string timePlayed;
                    int minutesAtStart = 0;
                    try
                    {
                        var rawMinutes = Convert.ToDouble(gameStats.StatListsCollection[0].Stats[0].Value);
                        minutesAtStart = (int)rawMinutes;
                        var t = TimeSpan.FromMinutes(rawMinutes);
                        timePlayed = $"{t.Days} Days, {t.Hours} Hours and {t.Minutes} minutes";
                    }
                    catch
                    {
                        timePlayed = "Unknown";
                    }

                    var item = new MultiSpoofGameItem
                    {
                        TitleId = title.TitleId ?? id,
                        Name = title.Name,
                        ImageUrl = !string.IsNullOrEmpty(title.DisplayImage) ? title.DisplayImage : "pack://application:,,,/Assets/cirno.png",
                        Gamerscore = gamerscore,
                        TimePlayed = timePlayed
                    };

                    lock (_multiStateLock)
                        _multiSessionStartMinutes[item.TitleId] = (item.Name, minutesAtStart);

                    System.Windows.Application.Current.Dispatcher.Invoke(() => MultiSpoofGames.Add(item));
                }
                catch
                {
                    lock (lockObj) failedIds.Add(id);
                }
            }).ToList();

            await Task.WhenAll(tasks);

            if (failedIds.Count > 0)
            {
                _snackbarService.Show("Some Title IDs failed",
                    $"Could not load info for: {string.Join(", ", failedIds)}",
                    ControlAppearance.Caution,
                    new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
            }
        }

        // Keeps every selected title marked as playing, in parallel, until the user stops it.
        // Each cycle sends presence for every title plus one heartbeat carrying the whole list, so all
        // the games accrue playtime together (leave 5 games running for 10h -> 10h on each of them).
        // The loop never ends on its own: rejected tokens and network errors are retried, which is
        // what used to make the session die after ~8-10h.
        private async Task MultiSpoofingLoop(List<string> titleIds, int session)
        {
            var stopwatch = Stopwatch.StartNew();
            var rotation = new List<string>(titleIds);
            var failures = 0;
            var parallel = false; // whether Xbox accepted the whole title list in one call
            var lastDeltaUpdate = DateTime.UtcNow;
            bool Stopped() => _multiSpoofingUpdate || session != _multiSpoofSession;

            var result = await SpoofSender.SendAsync(_multiSpoofApi.Value, HomeViewModel.XUIDOnly, rotation);

            while (!Stopped())
            {
                if (result.Success)
                {
                    failures = 0;
                    parallel = result.MultiTitleAccepted;

                    // Move the head to the back so a different title is sent last each cycle. When the
                    // whole list is accepted at once this changes nothing (they all stay active); when
                    // it isn't, the playtime gets shared evenly between the games instead of always
                    // landing on the same one.
                    if (rotation.Count > 1)
                    {
                        var first = rotation[0];
                        rotation.RemoveAt(0);
                        rotation.Add(first);
                    }

                    SaveMultiSpoofState(true, titleIds); // heartbeat -> refresh lastSavedAtUtc

                    if (DateTime.UtcNow - lastDeltaUpdate >= LiveDeltaInterval)
                    {
                        await UpdateLiveDeltasAsync(titleIds); // re-fetch playtime and show "+X min" live
                        lastDeltaUpdate = DateTime.UtcNow;
                    }
                }
                else
                {
                    failures++;
                    NotifySpoofProblem("Multi-Spoofing interrupted", result, failures);
                }

                var healthy = result.Success;
                var attempt = failures;
                var mode = parallel ? "in parallel" : "alternating";
                if (await WaitWhileSpoofingAsync(
                        healthy ? SpoofRefreshInterval : SpoofRetryInterval,
                        Stopped,
                        () =>
                        {
                            var elapsed = stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
                            foreach (var item in MultiSpoofGames)
                                item.SpoofingDuration = elapsed;
                            MultiSpoofingStatusText = healthy
                                ? $"Multi-Spoofing {rotation.Count} title(s) {mode} - {elapsed}"
                                : $"Reconnecting {rotation.Count} title(s) (attempt {attempt}) - {elapsed}";
                        }))
                    break;

                if (!HomeViewModel.IsSignedIn)
                {
                    result = SpoofResult.Fail("Waiting for the Xbox login to come back.");
                    continue;
                }

                result = await SpoofSender.SendAsync(_multiSpoofApi.Value, HomeViewModel.XUIDOnly, rotation);
            }
        }

        // ---- Persistencia de estado / historico / auto-retomada (padrao ScannerViewModel) ----

        private void SaveMultiSpoofState(bool active, List<string> titleIds)
        {
            try
            {
                Directory.CreateDirectory(XAUFolder);
                JObject obj;
                lock (_multiStateLock)
                {
                    var startObj = new JObject();
                    foreach (var kv in _multiSessionStartMinutes)
                    {
                        startObj[kv.Key] = new JObject
                        {
                            ["name"] = kv.Value.Name,
                            ["minutes"] = kv.Value.Minutes
                        };
                    }
                    obj = new JObject
                    {
                        ["active"] = active,
                        ["titleIds"] = new JArray(titleIds ?? new List<string>()),
                        ["startedAtUtc"] = _multiSessionStartedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["lastSavedAtUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        ["startMinutes"] = startObj
                    };
                }
                File.WriteAllText(MultiSpoofStatePath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch { /* nao critico */ }
        }

        private MultiSpoofState LoadMultiSpoofState()
        {
            try
            {
                if (!File.Exists(MultiSpoofStatePath)) return null;
                var obj = JObject.Parse(File.ReadAllText(MultiSpoofStatePath));
                var started = obj["startedAtUtc"]?.Value<DateTime?>();

                var startMinutes = new Dictionary<string, MultiSpoofBaselineEntry>();
                if (obj["startMinutes"] is JObject startObj)
                {
                    foreach (var prop in startObj.Properties())
                    {
                        startMinutes[prop.Name] = new MultiSpoofBaselineEntry
                        {
                            Name = prop.Value["name"]?.ToString() ?? "",
                            Minutes = prop.Value["minutes"]?.Value<int>() ?? 0
                        };
                    }
                }

                return new MultiSpoofState
                {
                    Active = obj["active"]?.Value<bool>() ?? false,
                    TitleIds = obj["titleIds"]?.ToObject<List<string>>() ?? new List<string>(),
                    StartedAtUtc = started.HasValue
                        ? DateTime.SpecifyKind(started.Value, DateTimeKind.Utc)
                        : DateTime.MinValue,
                    StartMinutes = startMinutes
                };
            }
            catch { return null; }
        }

        private MultiSpoofHistory LoadMultiSpoofHistory()
        {
            try
            {
                if (!File.Exists(MultiSpoofHistoryPath)) return new MultiSpoofHistory();
                return Newtonsoft.Json.JsonConvert.DeserializeObject<MultiSpoofHistory>(
                           File.ReadAllText(MultiSpoofHistoryPath)) ?? new MultiSpoofHistory();
            }
            catch { return new MultiSpoofHistory(); }
        }

        private void SaveMultiSpoofHistory(MultiSpoofSession session)
        {
            try
            {
                Directory.CreateDirectory(XAUFolder);
                var hist = LoadMultiSpoofHistory();
                hist.Sessions.Add(session);
                while (hist.Sessions.Count > 2) hist.Sessions.RemoveAt(0); // FIFO: guarda as 2 ultimas
                File.WriteAllText(MultiSpoofHistoryPath,
                    Newtonsoft.Json.JsonConvert.SerializeObject(hist, Newtonsoft.Json.Formatting.Indented));
            }
            catch { /* nao critico */ }
        }

        // Re-busca os minutos jogados ao parar e grava a sessao no historico.
        private async Task RecordMultiSpoofSessionEndAsync()
        {
            Dictionary<string, (string Name, int Minutes)> startSnapshot;
            lock (_multiStateLock) startSnapshot = new(_multiSessionStartMinutes);
            if (startSnapshot.Count == 0) return;

            var session = new MultiSpoofSession
            {
                StartedAtUtc = _multiSessionStartedAtUtc,
                EndedAtUtc = DateTime.UtcNow
            };

            foreach (var kv in startSnapshot)
            {
                int endMinutes = kv.Value.Minutes;
                try
                {
                    var stats = await _multiXboxRestAPI.Value.GetGameStatsAsync(HomeViewModel.XUIDOnly, kv.Key);
                    endMinutes = (int)Convert.ToDouble(stats.StatListsCollection[0].Stats[0].Value);
                }
                catch { /* mantem minutos do inicio se a re-busca falhar */ }

                session.Games.Add(new MultiSpoofGameDelta
                {
                    TitleId = kv.Key,
                    Name = kv.Value.Name,
                    MinutesAtStart = kv.Value.Minutes,
                    MinutesAtEnd = endMinutes,
                    Delta = endMinutes - kv.Value.Minutes
                });
            }

            SaveMultiSpoofHistory(session);
            ApplyLastSessionDeltas();
        }

        // Preenche LastSessionDelta de cada jogo carregado com base na ultima sessao do historico.
        private void ApplyLastSessionDeltas()
        {
            var hist = LoadMultiSpoofHistory();
            var last = hist.Sessions.LastOrDefault();
            if (last == null) return;
            var map = last.Games
                .GroupBy(g => g.TitleId)
                .ToDictionary(g => g.Key, g => g.Last().Delta);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in MultiSpoofGames)
                {
                    if (map.TryGetValue(item.TitleId, out var delta))
                        item.LastSessionDelta = delta > 0 ? $"Last session: +{delta} min" : "Last session: no increase";
                }
            });
        }

        // During the active session: re-fetch each title's playtime and show live how much has been
        // added since the session started (addresses "the games are increasing but it isn't displayed").
        private async Task UpdateLiveDeltasAsync(List<string> titleIds)
        {
            Dictionary<string, (string Name, int Minutes)> baseline;
            lock (_multiStateLock) baseline = new(_multiSessionStartMinutes);
            if (baseline.Count == 0) return;

            foreach (var id in titleIds)
            {
                if (!baseline.TryGetValue(id, out var start)) continue;

                int current;
                try
                {
                    var stats = await _multiXboxRestAPI.Value.GetGameStatsAsync(HomeViewModel.XUIDOnly, id);
                    current = (int)Convert.ToDouble(stats.StatListsCollection[0].Stats[0].Value);
                }
                catch { continue; } // keep the previous value if the re-fetch fails

                int delta = current - start.Minutes;
                var t = TimeSpan.FromMinutes(current);
                var playedStr = $"{t.Days} Days, {t.Hours} Hours and {t.Minutes} minutes";

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var item = MultiSpoofGames.FirstOrDefault(g => g.TitleId == id);
                    if (item == null) return;
                    item.TimePlayed = playedStr;
                    item.LastSessionDelta = delta > 0 ? $"+{delta} min this session" : "This session: no increase yet";
                });
            }
        }

        // Closes into history a session that stayed active but was never stopped (app closed/crash/restart),
        // using the persisted baseline. This caches the "last session" even without a clean stop.
        private async Task FinalizeInterruptedSessionAsync(MultiSpoofState state)
        {
            if (state?.StartMinutes == null || state.StartMinutes.Count == 0) return;

            var session = new MultiSpoofSession
            {
                StartedAtUtc = state.StartedAtUtc,
                EndedAtUtc = DateTime.UtcNow
            };

            foreach (var kv in state.StartMinutes)
            {
                int startMin = kv.Value.Minutes;
                int endMin = startMin;
                try
                {
                    var stats = await _multiXboxRestAPI.Value.GetGameStatsAsync(HomeViewModel.XUIDOnly, kv.Key);
                    endMin = (int)Convert.ToDouble(stats.StatListsCollection[0].Stats[0].Value);
                }
                catch { /* keep the baseline if the re-fetch fails */ }

                session.Games.Add(new MultiSpoofGameDelta
                {
                    TitleId = kv.Key,
                    Name = kv.Value.Name,
                    MinutesAtStart = startMin,
                    MinutesAtEnd = endMin,
                    Delta = endMin - startMin
                });
            }

            SaveMultiSpoofHistory(session);
        }

        // Chamado no startup (apos login): religa o multi-spoof silenciosamente se estava ativo ao fechar.
        public async Task TryAutoResumeMultiSpoof()
        {
            if (_multiAutoResumeTried) return;
            _multiAutoResumeTried = true;

            var state = LoadMultiSpoofState();
            if (state == null || !state.Active || state.TitleIds.Count == 0) return;
            if (_multiCurrentlySpoofing) return;

            // The previous session was never stopped (app closed/restart). Close it into history now,
            // using the persisted baseline, so the "last session" delta is cached and displayed.
            await FinalizeInterruptedSessionAsync(state);

            MultiSpoofingIDs = string.Join(", ", state.TitleIds);
            await MultiSpooferButtonClicked(); // reusa o caminho existente (Load + Loop + SaveState)
        }

        #endregion

        #region GameSearch
        [ObservableProperty] private List<GameItem> _tSearchResults = new List<GameItem>();
        [ObservableProperty] private List<string> _tSearchTitleNames = new List<string>();
        [ObservableProperty] private string _tSearchText = "";
        [ObservableProperty] private string _tSearchGameName = "Name: ";
        [ObservableProperty] private string _tSearchGameTitleID = "";
        [ObservableProperty] private string _tSearchGameTitleBased = "Title Based: Unknown";

        private string GetDatabasePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "TitleSearch", "xbox_games.db");
        }

        [RelayCommand]
        public async Task SearchGame()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(TSearchText))
                {
                    TSearchTitleNames = new List<string>();
                    TSearchResults = new List<GameItem>();
                    return;
                }

                string dbPath = GetDatabasePath();

                if (!File.Exists(dbPath))
                {
                    _snackbarService.Show("Error", "Game database not found. Please wait for it to download.",
                        ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }

                var results = await Task.Run(() => SearchGamesInDatabase(dbPath, TSearchText));

                if (!results.Any())
                {
                    _snackbarService.Show("Error", $"No results were found for '{TSearchText}'",
                        ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    TSearchTitleNames = new List<string>();
                    TSearchResults = new List<GameItem>();
                    return;
                }

                results = results.OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ToList();

                TSearchResults = results;
                TSearchTitleNames = results.Select(game => game.Title).ToList();
            }
            catch (Exception ex)
            {
                _snackbarService.Show("Error", $"Search failed: {ex.Message}",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                TSearchTitleNames = new List<string>();
                TSearchResults = new List<GameItem>();
            }
        }

        private List<GameItem> SearchGamesInDatabase(string dbPath, string searchText)
        {
            var results = new List<GameItem>();

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            // Search for games that contain the search text (case-insensitive)
            string sql = @"
                    SELECT title, titleId, isTitleBased 
                    FROM games 
                    WHERE title LIKE @searchText 
                    ORDER BY title COLLATE NOCASE
                    LIMIT 100";

            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@searchText", $"%{searchText}%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new GameItem
                {
                    Title = reader.GetString("title"),
                    TitleId = reader.GetString("titleId"),
                    IsTitleBased = reader.GetInt32("isTitleBased") == 1
                });
            }

            return results;
        }

        public void DisplayGameInfo(int index)
        {
            try
            {
                if (TSearchResults == null || TSearchResults.Count <= index || index < 0)
                {
                    _snackbarService.Show("Error", "No game found at the selected index.",
                        ControlAppearance.Danger,
                        new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }

                var selectedGame = TSearchResults[index];

                TSearchGameName = selectedGame.Title;
                TSearchGameTitleID = selectedGame.TitleId;
                TSearchGameTitleBased = $"Title Based: {(selectedGame.IsTitleBased ? "True" : "False")}";

            }
            catch (Exception ex)
            {
                _snackbarService.Show("Error", "Failed to display game info.",
                    ControlAppearance.Danger,
                    new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
        }

        #endregion

        #region GamertagSearch
        [ObservableProperty] private string _gamertag = "";
        [ObservableProperty] private string _gamertagName = "Gamertag:";
        [ObservableProperty] private string _gamertagImage = "pack://application:,,,/Assets/cirno.png";
        [ObservableProperty] private string _gamertagScore = "Gamerscore: ";
        [ObservableProperty] private string _gamertagXuid;
        [ObservableProperty] private bool _excludeZeroGamerscoreGames;
        [ObservableProperty] private bool _excludeXbox360Games;

        [RelayCommand]
        public async Task SearchGamertag()
        {
            if (string.IsNullOrWhiteSpace(Gamertag))
            {
                _snackbarService.Show("Error", "Please enter a valid gamertag.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            var profileData = await _xboxRestAPI.Value.GetGamertagProfileAsync(Gamertag) ?? new JObject();
            var profileUsers = profileData["profileUsers"]?.FirstOrDefault();
            if (profileUsers == null)
            {
                _snackbarService.Show("Error", "Failed to fetch gamertag information.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }

            GamertagXuid = profileUsers["id"]?.ToString() ?? string.Empty;
            GamertagName = "Gamertag: " + profileUsers["settings"]?.FirstOrDefault(setting => setting["id"]?.ToString() == "Gamertag")?["value"]?.ToString() ?? "Unknown";
            GamertagScore = "Gamerscore: " + profileUsers["settings"]?.FirstOrDefault(setting => setting["id"]?.ToString() == "Gamerscore")?["value"]?.ToString() ?? "Unknown";
            GamertagImage = profileUsers["settings"]?.FirstOrDefault(setting => setting["id"]?.ToString() == "GameDisplayPicRaw")?["value"]?.ToString()?.Replace("&mode=Padding", "") ?? string.Empty;

        }

        public async Task ExportToCsvAsync()
        {
            if (string.IsNullOrWhiteSpace(GamertagXuid))
            {
                _snackbarService.Show("Error", "Search for a user first.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                return;
            }
            try
            {
                _snackbarService.Show("Fetching Games", "Trying to get games. This may take a moment depending on the number of games the user has.", ControlAppearance.Primary, new SymbolIcon(SymbolRegular.XboxController24), _snackbarDuration);
                var gamesResponse = await _xboxRestAPI.Value.GetGamesListAsync(GamertagXuid);

                if (gamesResponse == null || gamesResponse.Titles == null)
                {
                    await Task.Delay(2500);
                    _snackbarService.Show("Error", "Failed to fetch games list.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }

                if (gamesResponse.Titles.Count == 0)
                {
                    await Task.Delay(2500);
                    _snackbarService.Show("No Titles Found", "No games found for this user. This could be due to user privacy settings or other reasons.", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("\"Title ID\",\"Title\",\"CurrentAchievements\",\"Gamerscore\",\"Progress\",\"Devices\",\"Genres\"");

                foreach (var title in gamesResponse.Titles)
                {
                    if (ExcludeZeroGamerscoreGames && title.Achievement.TotalGamerscore == 0)
                    {
                        continue;
                    }

                    if (ExcludeXbox360Games && title.Devices != null && title.Devices.Contains("Xbox360"))
                    {
                        continue;
                    }

                    var titleName = title.Name.Replace("\"", "\"\"");
                    var devices = title.Devices != null ? string.Join(", ", title.Devices).Replace("\"", "\"\"") : string.Empty;
                    var genres = title.Detail?.Genres != null ? string.Join(", ", title.Detail.Genres).Replace("\"", "\"\"") : string.Empty;

                    sb.AppendLine($"\"{title.TitleId}\",\"{titleName}\",\"{title.Achievement.CurrentAchievements}\",\"{title.Achievement.CurrentGamerscore}/{title.Achievement.TotalGamerscore}\",\"{title.Achievement.ProgressPercentage}\",\"{devices}\",\"{genres}\"");
                }

                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv",
                    FileName = $"{GamertagXuid}.csv"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    await Task.Run(() =>
                    {
                        File.WriteAllText(saveFileDialog.FileName, sb.ToString());
                    });

                    _snackbarService.Show("Success", "Games list exported successfully.", ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), _snackbarDuration);
                }
                else
                {
                    _snackbarService.Show("Cancelled", "Game export was not completed", ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), _snackbarDuration);
                }
            }
            catch (Exception ex)
            {
                _snackbarService.Show("Error", "Failed to export games list: " + ex.Message, ControlAppearance.Danger, new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
            }
        }
        #endregion
    }
}
