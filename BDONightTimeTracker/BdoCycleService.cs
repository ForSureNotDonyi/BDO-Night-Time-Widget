using System.IO;
using System.Text.Json;

namespace BDONightTimeTracker;

/// <summary>
/// Gestisce la simulazione del ciclo giorno/notte di BDO.
///
/// IL CICLO BDO (valori verificati):
///   Ciclo completo = 4 ore reali (240 minuti)
///   ┌─ GIORNO: 07:00–22:00 in-game (15 ore di gioco)  =  200 min reali  (3h20m)
///   └─ NOTTE:  22:00–07:00 in-game  (9 ore di gioco)  =   40 min reali
///
/// I rapporti sono DIVERSI tra giorno e notte:
///   Giorno: 1 min reale = 4.5  min di gioco  (900 / 200)
///   Notte:  1 min reale = 13.5 min di gioco  (540 / 40)
///
/// STRATEGIA INTERNA:
/// Invece di lavorare direttamente in "minuti di gioco" (che avanzano a velocità variabile),
/// uso la "posizione nel ciclo reale" come grandezza intermedia lineare.
///   realCyclePos ∈ [0, 240)  dove 0 = inizio giorno (7:00), 200 = inizio notte (22:00)
/// Da realCyclePos si ricavano gameTime, fase e countdown con funzioni piecewise semplici.
/// </summary>
public class BdoCycleService
{
    // --- Costanti del ciclo (in minuti) ---
    private const double DayRealDuration   = 200.0;  // 3h20m reali
    private const double NightRealDuration =  40.0;  // 40m reali
    private const double TotalRealCycle    = 240.0;  // 4h reali

    private const int DayStartGame    =  7 * 60;   //  420 min = 07:00 in-game
    private const int NightStartGame  = 22 * 60;   // 1320 min = 22:00 in-game
    private const int DayGameDuration   = 900;     // 15h in-game = NightStartGame - DayStartGame
    private const int NightGameDuration = 540;     //  9h in-game = 1440 - DayGameDuration

    // --- Stato interno ---
    private DateTime _anchorRealTime;
    private double   _anchorRealCyclePos; // posizione nel ciclo reale al momento dell'anchor [0, 240)

    public bool IsAnchored { get; private set; }

    // --- Conversioni piecewise ---

    /// <summary>
    /// Da ora di gioco (minuti 0–1439) alla posizione nel ciclo reale [0, 240).
    /// Usata per convertire l'input dell'utente/OCR in formato interno.
    /// </summary>
    private static double GameMinutesToRealCyclePos(int gameMinutes)
    {
        if (gameMinutes >= DayStartGame && gameMinutes < NightStartGame)
        {
            // Fase giorno: mappa [420, 1320) → [0, 200)
            return (gameMinutes - DayStartGame) / (double)DayGameDuration * DayRealDuration;
        }
        else
        {
            // Fase notte: mappa [1320, 1440) ∪ [0, 420) → [200, 240)
            int nightElapsed = gameMinutes >= NightStartGame
                ? gameMinutes - NightStartGame
                : gameMinutes + (1440 - NightStartGame); // wrap dopo mezzanotte
            return DayRealDuration + nightElapsed / (double)NightGameDuration * NightRealDuration;
        }
    }

    /// <summary>
    /// Da posizione nel ciclo reale [0, 240) all'ora di gioco (minuti 0–1439).
    /// </summary>
    private static int RealCyclePosToGameMinutes(double pos)
    {
        pos = ((pos % TotalRealCycle) + TotalRealCycle) % TotalRealCycle; // normalizza in [0, 240)

        if (pos < DayRealDuration)
        {
            // Fase giorno
            return DayStartGame + (int)(pos / DayRealDuration * DayGameDuration);
        }
        else
        {
            // Fase notte
            int nightGame = (int)((pos - DayRealDuration) / NightRealDuration * NightGameDuration);
            return (NightStartGame + nightGame) % 1440;
        }
    }

    // --- API pubblica ---

    /// <summary>
    /// Registra l'anchor point. Da chiamare nel momento esatto in cui si legge
    /// l'ora dal gioco (manualmente o via OCR): cattura automaticamente DateTime.Now.
    /// </summary>
    public void SetAnchor(int gameHour, int gameMinute) => SetAnchor(gameHour, gameMinute, DateTime.Now);

    /// <summary>
    /// Come SetAnchor, ma permette di specificare il momento reale esatto a cui
    /// corrisponde la lettura (es. l'istante calcolato da una calibrazione
    /// edge-triggered, invece del momento in cui SetAnchor viene chiamato).
    /// </summary>
    public void SetAnchor(int gameHour, int gameMinute, DateTime anchorRealTime)
    {
        _anchorRealTime     = anchorRealTime;
        _anchorRealCyclePos = GameMinutesToRealCyclePos(gameHour * 60 + gameMinute);
        IsAnchored          = true;
        SaveToFile();
    }

    /// <summary>Posizione corrente nel ciclo reale [0, 240), aggiornata ogni chiamata.</summary>
    private double GetCurrentRealCyclePos()
    {
        double minutesElapsed = (DateTime.Now - _anchorRealTime).TotalMinutes;
        return ((_anchorRealCyclePos + minutesElapsed) % TotalRealCycle + TotalRealCycle) % TotalRealCycle;
    }

    /// <summary>Ora di gioco corrente, in minuti dall'inizio del giorno (0–1439).</summary>
    public int GetCurrentGameMinutes()
    {
        if (!IsAnchored) return 0;
        return RealCyclePosToGameMinutes(GetCurrentRealCyclePos());
    }

    /// <summary>true = fase diurna (07:00–22:00 in-game), ovvero pos reale &lt; 200.</summary>
    public bool IsDay()
    {
        if (!IsAnchored) return true;
        return GetCurrentRealCyclePos() < DayRealDuration;
    }

    /// <summary>
    /// Tempo reale rimanente (come TimeSpan) fino alla prossima transizione giorno↔notte.
    /// </summary>
    public TimeSpan GetTimeUntilTransition()
    {
        double pos = GetCurrentRealCyclePos();
        double remainingMinutes = pos < DayRealDuration
            ? DayRealDuration   - pos
            : TotalRealCycle    - pos;
        return TimeSpan.FromMinutes(remainingMinutes);
    }

    /// <summary>Avanzamento nella fase corrente: 0.0 = inizio, 1.0 = fine. Per la progress bar.</summary>
    public double GetPhaseProgress()
    {
        if (!IsAnchored) return 0;
        double pos = GetCurrentRealCyclePos();
        return pos < DayRealDuration
            ? pos / DayRealDuration
            : (pos - DayRealDuration) / NightRealDuration;
    }

    // --- Persistenza ---

    private static string AnchorFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BDONightTimeTracker", "anchor.json");

    public void SaveToFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AnchorFilePath)!);
        // Salva l'ora di gioco (non realCyclePos) per leggibilità umana del JSON.
        int gm = RealCyclePosToGameMinutes(_anchorRealCyclePos);
        File.WriteAllText(AnchorFilePath,
            JsonSerializer.Serialize(new AnchorData(_anchorRealTime, gm / 60, gm % 60)));
    }

    public void LoadFromFile()
    {
        if (!File.Exists(AnchorFilePath)) return;
        try
        {
            var d = JsonSerializer.Deserialize<AnchorData>(File.ReadAllText(AnchorFilePath));
            if (d is null) return;
            _anchorRealTime     = d.RealTime;
            _anchorRealCyclePos = GameMinutesToRealCyclePos(d.GameHour * 60 + d.GameMinute);
            IsAnchored          = true;
        }
        catch
        {
            // File corrotto: utente dovrà risincronizzare (oppure l'OCR automatico lo farà).
        }
    }

    // GameHour + GameMinute invece di GameMinutes: più leggibile nel JSON
    private record AnchorData(DateTime RealTime, int GameHour, int GameMinute);
}
