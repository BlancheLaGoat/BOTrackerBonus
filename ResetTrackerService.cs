using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BO1Tracker;

// ================================================================
//  RESET TRACKER — portage du ResetTracker.cpp original
// ================================================================
//
// Lit UNIQUEMENT deux valeurs en mémoire de BlackOps.exe (jamais
// d'écriture, jamais d'injection) : le "level time" (compteur de
// ticks interne) et le nombre d'entités sur la map. À très haute
// manche, ce compteur de ticks peut déborder ("reset"), ce qui
// provoque souvent un crash de la partie. Ce tracker permet
// d'estimer combien de temps il reste avant que ça arrive.
//
// PROCESS_VM_READ + PROCESS_QUERY_INFORMATION seulement : ce sont
// des droits de LECTURE, le processus du jeu n'est jamais modifié.
// ================================================================

class ResetTrackerSnapshot
{
    public bool   ProcessFound;
    public int    Pid;
    public string ProcessName = "";
    public int    LevelTime;
    public int    EntityCount;
    public int    LastSnapshotTicks;
    public TimeSpan SessionElapsed;
    public TimeSpan? EstimatedTimeToReset; // null si non calculable
}

class ResetTrackerService : IDisposable
{
    // Deux façons de lancer BO1 : directement (BlackOps.exe) ou via Plutonium
    // (plutonium-bootstrapper-win32.exe). On essaie les deux noms de process.
    private static readonly string[] GameProcessNames = { "BlackOps", "plutonium-bootstrapper-win32" };
    private const int ADDR_LEVEL_TIME   = 0x286D034;
    private const int ADDR_ENTITY_COUNT = 0x29A5608;
    private const long RESET_MAX = 2147483647L;

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ            = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _hProcess = IntPtr.Zero;
    private int    _pid      = 0;
    private string _processName = "";

    private DateTime _sessionStart = DateTime.Now;
    private int       _lastSnapshotTicks  = 0;
    private bool      _snapshotTakenThisMinute = false;

    /// <summary>Prend une "photo" de l'état actuel. À appeler périodiquement (ex: toutes les 250-500ms).</summary>
    public ResetTrackerSnapshot Poll()
    {
        // Le process peut apparaître/disparaître (jeu pas lancé, fermé, relancé) : on revérifie à chaque appel.
        if (_hProcess == IntPtr.Zero || !IsProcessAlive(_pid))
        {
            CloseHandleIfOpen();
            var proc = FindGameProcess();
            if (proc == null)
                return new ResetTrackerSnapshot { ProcessFound = false };

            _pid = proc.Id;
            _processName = proc.ProcessName;
            _hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, _pid);
            if (_hProcess == IntPtr.Zero)
                return new ResetTrackerSnapshot { ProcessFound = false };

            ResetSession();
        }

        int levelTime   = ReadInt(ADDR_LEVEL_TIME);
        int entityCount = ReadInt(ADDR_ENTITY_COUNT);

        // Détecte un reset (le compteur repart de 0)
        if (levelTime <= 0)
            ResetSession();

        var elapsed = DateTime.Now - _sessionStart;
        long elapsedSec = (long)elapsed.TotalSeconds;

        if (elapsedSec > 0 && elapsedSec % 60 == 0 && !_snapshotTakenThisMinute)
        {
            _lastSnapshotTicks = levelTime;
            _snapshotTakenThisMinute = true;
        }
        if (elapsedSec % 60 != 0)
            _snapshotTakenThisMinute = false;

        return new ResetTrackerSnapshot
        {
            ProcessFound          = true,
            Pid                   = _pid,
            ProcessName           = _processName,
            LevelTime             = levelTime,
            EntityCount           = entityCount,
            LastSnapshotTicks     = _lastSnapshotTicks,
            SessionElapsed        = elapsed,
            EstimatedTimeToReset  = EstimateTimeToReset(levelTime, entityCount),
        };
    }

    private void ResetSession()
    {
        _sessionStart = DateTime.Now;
        _lastSnapshotTicks = 0;
        _snapshotTakenThisMinute = false;
    }

    private static Process? FindGameProcess()
    {
        foreach (var name in GameProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try { var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private int ReadInt(int address)
    {
        var buffer = new byte[4];
        if (!ReadProcessMemory(_hProcess, (IntPtr)address, buffer, 4, out int bytesRead) || bytesRead != 4)
            return 0;
        return BitConverter.ToInt32(buffer, 0);
    }

    /// <summary>Même formule que l'original : le moteur BO1 tourne à 20 Hz (tick = 50ms).</summary>
    private static TimeSpan? EstimateTimeToReset(int levelTime, int entityCount)
    {
        if (entityCount <= 0 || levelTime <= 0) return null;
        long remaining  = RESET_MAX - levelTime;
        long ticksLeft  = remaining / entityCount;
        long totalMs    = ticksLeft * 50L;
        return TimeSpan.FromMilliseconds(totalMs);
    }

    private void CloseHandleIfOpen()
    {
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        _pid = 0;
    }

    public void Dispose() => CloseHandleIfOpen();
}
