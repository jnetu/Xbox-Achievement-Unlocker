# Prompt — Persistência e auto-retomada do Multi-Spoofer / Auto-Unlock (XAU)

## Contexto do projeto
Projeto WPF (.NET) MVVM usando CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`) e Newtonsoft.Json.

Arquivos relevantes:
- `ViewModels/Pages/MiscViewModel.cs` — região `#region MultiSpoofer` (estado: `MultiSpoofingIDs`, `MultiSpoofGames`, `_multiCurrentlySpoofing`, `MultiSpoofingLoop`, `LoadMultiSpoofGamesAsync`, `MultiSpooferButtonClicked`).
- `ViewModels/Pages/AchievementsViewModel.cs` — auto-unlock (`IsAutoUnlocking`, `AutoUnlockLoop`, `_autoUnlockCts`, `AutoUnlockMinMinutes/MaxMinutes`).
- `Models/MiscItems.cs` — `MultiSpoofGameItem` (`TitleId`, `Name`, `ImageUrl`, `Gamerscore`, `TimePlayed`, `SpoofingDuration`).
- `ViewModels/Pages/HomeViewModel.cs` — carga/gravação de `settings.json` em `Documents\XAU` (`Settings`, `LoadSettings`, `SaveSettings`), e é onde o app inicializa no startup.
- `ViewModels/Pages/ScannerViewModel.cs` (linhas ~110-200) — **use como referência de estilo**: `LoadScanState`/`SaveScanState` com `JObject`, `Directory.CreateDirectory`, `try/catch` silencioso, timestamps em UTC ISO-8601 (`"o"`, `CultureInfo.InvariantCulture`).
- Horas jogadas vêm de `GetGameStatsAsync(...).StatListsCollection[0].Stats[0].Value` (em minutos) — mesma fonte usada em `LoadMultiSpoofGamesAsync`.

Pasta de dados: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU")`.

## Objetivo
Ao reabrir o XAU, retomar automaticamente o Multi-Spoofing (e/ou Auto-Unlock) de onde parou, e registrar histórico das últimas sessões para verificar se as horas de cada jogo realmente aumentaram.

## Requisitos

### 1. Persistir o estado do Multi-Spoofer
Criar `Documents\XAU\multispoof_state.json` contendo:
- `active` (bool) — se o multi-spoof estava ligado ao fechar.
- `titleIds` (lista de strings) — exatamente a lista de gameIds que estava aberta.
- `startedAtUtc` — quando a sessão atual começou.
- `lastSavedAtUtc`.

Gravar quando: iniciar multi-spoof, parar multi-spoof, e periodicamente dentro do `MultiSpoofingLoop` (ex.: a cada heartbeat / a cada N segundos). Limpar/`active=false` ao parar manualmente.

### 2. Auto-retomada SILENCIOSA no startup
No fluxo de inicialização (após login/auth pronto, onde `HomeViewModel.Settings` já carregou), a retomada deve ser **silenciosa** — religar sozinho, sem dialog/snackbar de confirmação e sem interação do usuário.

**Multi-Spoofer:** se `multispoof_state.json` tiver `active == true`:
- recarregar `MultiSpoofingIDs` com a lista anterior de `titleIds` salvos;
- chamar o caminho existente de start (reaproveitar `LoadMultiSpoofGamesAsync` + `MultiSpoofingLoop`, **não** duplicar lógica);
- atualizar `MultiSpoofingButtonText`/`MultiSpoofingStatusText` coerentemente.

**Auto-Unlock:** se estava ativo ao fechar (`active == true` no estado dele), religar sozinho com **os mesmos parâmetros `MinMinutes`/`MaxMinutes`** salvos. Persistir esse estado num `Documents\XAU\autounlock_state.json` (`active`, `minMinutes`, `maxMinutes`, `titleId` se aplicável), gravado ao iniciar/parar o auto-unlock em `AchievementsViewModel`.

**Ordenação por % de desbloqueio:** essa ordenação **já existe** em `ToggleAutoUnlock` (`AchievementsViewModel.cs`, ~linha 692), que monta a fila com `.Where(a => a.IsUnlockable).OrderByDescending(a => a.RarityPercentage)` e também reordena `DGAchievements` por `RarityPercentage` desc (mais comuns → mais raros). O campo é `RarityPercentage` (float), parseado de `raritycurrentPercentage`. **Requisito:** garantir que o caminho de retomada silenciosa reutilize exatamente esse mesmo `ToggleAutoUnlock`/construção de fila — para que a fila ressurja já ordenada por `RarityPercentage` desc. **Não** crie uma segunda lógica de ordenação; apenas reaproveite a existente.

Não é necessária flag de opt-out: a retomada é sempre silenciosa por padrão.

### 3. Histórico das 2 últimas sessões + delta de horas
Criar `Documents\XAU\multispoof_history.json` que guarda as **2 últimas sessões** (FIFO, descarta a mais antiga). Cada sessão registra, por `titleId`:
- `name`;
- `minutesAtStart` e `minutesAtEnd` (minutos de tempo jogado, da mesma fonte de stats);
- `delta = minutesAtEnd - minutesAtStart`;
- timestamps `startedAtUtc`/`endedAtUtc`.

Capturar `minutesAtStart` ao iniciar a sessão (já temos os stats em `LoadMultiSpoofGamesAsync`) e `minutesAtEnd` ao parar (re-buscar stats ou usar o último heartbeat). Expor no `MiscViewModel` algo consumível pela UI indicando, por jogo, se houve aumento de horas na última sessão (ex.: `+12 min` ou "sem aumento") — isso ajuda a confirmar que o spoof funcionou.

### 4. UI (mínima)
Em `Views/Pages/MiscPage.xaml`, na seção do Multi-Spoofer, mostrar para cada `MultiSpoofGameItem` o delta da última sessão. Adicionar coluna/propriedade nova em `MultiSpoofGameItem` se necessário (ex.: `[ObservableProperty] string _lastSessionDelta`).

## Restrições e estilo
- Siga o padrão de `ScannerViewModel` para I/O de JSON: `JObject`/`JsonConvert`, `try/catch` silencioso, `Directory.CreateDirectory`, UTC ISO-8601.
- Não quebre as assinaturas existentes de `MultiSpooferButtonClicked`, `LoadMultiSpoofGamesAsync`, `MultiSpoofingLoop`; reaproveite-as.
- Toda atualização de `ObservableCollection`/propriedades de UI deve usar `Application.Current.Dispatcher.Invoke`, como já é feito.
- Thread-safe: a gravação de estado pode ocorrer dentro do loop em background.
- Não introduza dependências novas.

## Entregáveis
1. Novo(s) model(s) de estado/histórico (pode ser em `Models/MiscItems.cs` ou arquivo novo `Models/MultiSpoofState.cs`).
2. Métodos `SaveMultiSpoofState`/`LoadMultiSpoofState`, `SaveMultiSpoofHistory`/`LoadMultiSpoofHistory` no `MiscViewModel`.
3. Hook de auto-retomada no startup (HomeViewModel/App).
4. Flag(s) em `XAUSettings` + binding na `SettingsPage` se aplicável.
5. Ajuste de UI no `MiscPage.xaml`.

## Antes de codar
Liste os pontos de inserção exatos (arquivo + método) e confirme o ponto de startup onde a retomada deve ser disparada. Depois implemente incrementalmente e garanta que compila (`dotnet build`).
