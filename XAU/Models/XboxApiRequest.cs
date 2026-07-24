// TODO: Clean up, set names, default fields, minor renames, etc.

using Newtonsoft.Json;

public class GameTitleRequest
{
    public string? Pfns { get; set; }
    public List<string> TitleIds { get; set; } = new List<string>();
}

public class AchievementsArrayEntry
{
    public string? id { get; set; }
    public string percentComplete { get; set; } = "100";
}

public class UnlockTitleBasedAchievementRequest
{
    public string action { get; set; } = @"progressUpdate";
    public string serviceConfigId { get; set; } = StringConstants.ZeroUid;
    public string? titleId { get; set; }
    public string? userId { get; set; }
    public List<AchievementsArrayEntry> achievements { get; set; } = new List<AchievementsArrayEntry>();
}

public class GameStat
{
    public string Name { get; set; } = "MinutesPlayed";
    public string? TitleId { get; set; }
}

public class GameStatsRequest
{
    public string ArrangeByField { get; set; } = "xuid";
    public List<string> Xuids { get; set; } = new List<string>();
    public List<GameStat> Stats { get; set; } = new List<GameStat>();
}


public class HeartbeatRequest
{
    public List<TitleRequest> titles { get; set; } = new List<TitleRequest>();
}

public class TitleRequest
{
    public int expiration { get; set; } = 600;
    public string? id { get; set; }
    public string state { get; set; } = "active";
    public string sandbox { get; set; } = "RETAIL";
}

// Body for the newer userpresence endpoint (single title).
public class PresenceTitleRequest
{
    [JsonProperty("id")]
    public ulong id { get; set; }

    public string state { get; set; } = "active";
    public string placement { get; set; } = "full";
}

// Result of a spoof attempt, so callers can surface the exact API error (e.g. 403).
public readonly struct SpoofResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    // The API rejected the token (401/403). The caller renews the presence token and retries instead
    // of killing the session -- this is what happens once the spoof token ages out mid-session.
    public bool AuthRejected { get; init; }

    // Every requested title was registered in a single call (multi-title heartbeat accepted), so all
    // of them stay "active" at the same time. When false the caller rotates the title order so the
    // playtime is at least spread evenly instead of piling up on one game.
    public bool MultiTitleAccepted { get; init; }

    public static SpoofResult Ok(bool multiTitleAccepted = false) =>
        new() { Success = true, MultiTitleAccepted = multiTitleAccepted };

    public static SpoofResult Fail(string error, bool authRejected = false) =>
        new() { Success = false, Error = error, AuthRejected = authRejected };
}

public class GamepassProductsRequest
{
    public List<string> Products { get; set; } = new List<string>();
}
