using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BDONightTimeTracker;

/// <summary>
/// Monitora il processo BDO e lo stato della sua finestra principale.
///
/// DESIGN: usa polling (1 call per tick del timer, ~1s) invece di SetWinEventHook
/// per semplicità e compatibilità. Il polling a 1 Hz è trascurabile per la CPU.
///
/// NOME DEL PROCESSO: "BlackDesert64" è il più comune per il client a 64 bit.
/// Se non viene rilevato, verifica con Task Manager il nome esatto e aggiungilo
/// all'array ProcessNames qui sotto.
/// </summary>
public class BdoProcessWatcher
{
    private static readonly string[] ProcessNames =
    [
        "BlackDesert64",   // client standard 64-bit (più comune)
        "BlackDesert",     // client alternativo / 32-bit
        "BLACKDESERT64",   // alcune versioni hanno il nome tutto maiuscolo
    ];

    [DllImport("user32.dll")] private static extern bool   IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private IntPtr _hwnd;
    private bool   _wasMinimized;
    private bool   _running;

    public bool   IsBdoRunning    => _running;
    public IntPtr BdoWindowHandle => _hwnd;
    public bool   IsBdoFocused    => _running && !_wasMinimized && GetForegroundWindow() == _hwnd;

    // --- Eventi (sollevati sul thread chiamante = thread UI del DispatcherTimer) ---

    /// <summary>BDO è stato rilevato (processo avviato o già in esecuzione all'avvio del widget).</summary>
    public event Action? BdoStarted;

    /// <summary>Il processo BDO non è più rilevabile (chiuso o terminato).</summary>
    public event Action? BdoStopped;

    /// <summary>La finestra di BDO è stata minimizzata.</summary>
    public event Action? BdoMinimized;

    /// <summary>La finestra di BDO è stata ripristinata dallo stato minimizzato.</summary>
    public event Action? BdoRestored;

    /// <summary>
    /// Da chiamare ogni tick del timer.
    /// Controlla lo stato del processo e della finestra, e solleva gli eventi appropriati.
    /// </summary>
    public void Poll()
    {
        IntPtr hwnd = FindBdoWindow();

        if (hwnd != IntPtr.Zero)
        {
            if (!_running)
            {
                // BDO appena rilevato (primo poll o riavvio)
                _hwnd         = hwnd;
                _running      = true;
                _wasMinimized = IsIconic(hwnd);
                BdoStarted?.Invoke();
            }
            else
            {
                _hwnd = hwnd; // aggiorna: il MainWindowHandle può cambiare raramente

                bool nowMinimized = IsIconic(hwnd);
                if (!_wasMinimized && nowMinimized)
                {
                    _wasMinimized = true;
                    BdoMinimized?.Invoke();
                }
                else if (_wasMinimized && !nowMinimized)
                {
                    _wasMinimized = false;
                    BdoRestored?.Invoke();
                }
            }
        }
        else if (_running)
        {
            // BDO è stato chiuso
            _hwnd         = IntPtr.Zero;
            _running      = false;
            _wasMinimized = false;
            BdoStopped?.Invoke();
        }
        // else: BDO non era in esecuzione e non lo è ancora → nessun evento
    }

    // Cerca il processo BDO tra i nomi noti e ne restituisce il MainWindowHandle.
    // Restituisce IntPtr.Zero se il processo non è trovato o non ha ancora una finestra.
    private static IntPtr FindBdoWindow()
    {
        foreach (string name in ProcessNames)
        {
            Process[] procs = Process.GetProcessesByName(name);
            foreach (Process p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            }
        }
        return IntPtr.Zero;
    }
}
