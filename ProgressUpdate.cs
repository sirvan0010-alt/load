namespace MailLoadTester;

public readonly record struct ProgressUpdate(
    int Sent,
    int Failed,
    string Status,
    double? EtaSeconds,
    int Phase,
    string CurrentStep,
    string NextStep,
    TestPhase TestPhase = TestPhase.Idle,
    MessageStep? MessageStep = null,
    int? MessageIndex = null,
    int? WorkerId = null,
    int WorkerCount = 0,
    /// <summary>Aktuální krok cesty odesílání (pro pipeline GUI, která se zbarvuje).</summary>
    DeliveryStepKind? PathStep = null,
    /// <summary>Zda poslední path krok uspěl (true=zelená, false=červená).</summary>
    bool? PathStepSuccess = null,
    /// <summary>Lidský text stavu tempa (burst pauza, greylist, time window…).</summary>
    string? PaceStatus = null,
    /// <summary>Efektivní interval ms po jitter/backoff.</summary>
    int? EffectiveIntervalMs = null,
    /// <summary>Agregované pozorované SMTP odpovědi (pro živou tabulku v GUI).</summary>
    IReadOnlyList<ObservedResponseRow>? ObservedSnapshot = null);
