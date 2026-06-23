using System;
using System.Collections.Generic;

// Estado persistido do Multi-Spoofer (Documents\XAU\multispoof_state.json).
public class MultiSpoofState
{
    public bool Active { get; set; }
    public List<string> TitleIds { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastSavedAtUtc { get; set; }
}

// Estado persistido do Auto-Unlock (Documents\XAU\autounlock_state.json).
public class AutoUnlockState
{
    public bool Active { get; set; }
    public int MinMinutes { get; set; }
    public int MaxMinutes { get; set; }
    public string TitleId { get; set; } = "0";
}

// Historico das ultimas sessoes de multi-spoof (Documents\XAU\multispoof_history.json).
public class MultiSpoofHistory
{
    public List<MultiSpoofSession> Sessions { get; set; } = new();
}

public class MultiSpoofSession
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndedAtUtc { get; set; }
    public List<MultiSpoofGameDelta> Games { get; set; } = new();
}

public class MultiSpoofGameDelta
{
    public string TitleId { get; set; } = "";
    public string Name { get; set; } = "";
    public int MinutesAtStart { get; set; }
    public int MinutesAtEnd { get; set; }
    public int Delta { get; set; }
}
