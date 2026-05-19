using System.Diagnostics;
using System.Management;
using Log.Interface;

namespace SisterBotService;

/// <summary>
/// Gestisce la pulizia all'avvio del servizio: termina processi ChromeDriver rimasti attivi e pulisce cartelle temporanee
/// </summary>
internal static class StartupCleanup
{
    #region Public Methods

    /// <summary>
    /// Esegue la pulizia completa all'avvio del servizio
    /// </summary>
    public static async Task ExecuteCleanupAsync(IWriter? logger = null)
    {
        logger?.WriteLog("Avvio procedura di pulizia iniziale...", LogEntryType.Information);

        // 1. Termina tutti i processi ChromeDriver attivi
        await KillChromeDriverProcessesAsync(logger);

        // 2. Pulisce le cartelle temporanee
        await CleanupTempDirectoriesAsync(logger);

        logger?.WriteLog("Procedura di pulizia iniziale completata.", LogEntryType.Information);
    }

    #endregion Public Methods

    #region Private Methods

    /// <summary>
    /// Pulisce tutte le directory temporanee che iniziano con "SisterBot" nella cartella Temp
    /// </summary>
    private static async Task CleanupTempDirectoriesAsync(IWriter? logger)
    {
        try
        {
            var tempPath = @"C:\Temp";
            logger?.WriteLog($"Ricerca cartelle temporanee in: {tempPath}", LogEntryType.Information);

            if (!Directory.Exists(tempPath))
            {
                logger?.WriteLog("Cartella Temp non trovata.", LogEntryType.Warning);
                return;
            }

            // Cerca tutte le directory che iniziano con "SisterBot"
            var sisterBotDirs = Directory.GetDirectories(tempPath, "SisterBot*", SearchOption.TopDirectoryOnly);

            if (sisterBotDirs.Length == 0)
            {
                logger?.WriteLog("Nessuna cartella temporanea SisterBot trovata.", LogEntryType.Information);
                return;
            }

            logger?.WriteLog($"Trovate {sisterBotDirs.Length} cartelle temporanee da eliminare.", LogEntryType.Information);

            var deletedCount = 0;
            var failedCount = 0;

            foreach (var dir in sisterBotDirs)
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    logger?.WriteLog($"Eliminazione cartella: {dirInfo.Name}", LogEntryType.Information);

                    // Rimuovi attributi readonly ricorsivamente
                    await Task.Run(() => RemoveReadOnlyAttribute(dirInfo));

                    // Elimina la directory e tutto il contenuto
                    Directory.Delete(dir, recursive: true);

                    deletedCount++;
                    logger?.WriteLog($"Cartella eliminata: {dirInfo.Name}", LogEntryType.Information);
                }
                catch (UnauthorizedAccessException ex)
                {
                    failedCount++;
                    logger?.WriteLog($"Accesso negato durante l'eliminazione di {dir}: {ex.Message}",
                        LogEntryType.Warning);
                }
                catch (IOException ex)
                {
                    failedCount++;
                    logger?.WriteLog($"Errore I/O durante l'eliminazione di {dir}: {ex.Message}",
                        LogEntryType.Warning);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    logger?.WriteLog($"Errore durante l'eliminazione di {dir}: {ex.Message}",
                        LogEntryType.Error);
                }
            }

            logger?.WriteLog(
                $"Pulizia cartelle temporanee completata. Eliminate: {deletedCount}, Fallite: {failedCount}",
                LogEntryType.Information);
        }
        catch (Exception ex)
        {
            logger?.WriteLog($"Errore durante la pulizia delle cartelle temporanee: {ex.Message}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Ottiene tutti i processi figli di un processo specificato usando una singola query WMI (molto più veloce)
    /// </summary>
    private static List<Process> GetChildProcesses(int parentProcessId)
    {
        var childProcesses = new List<Process>();

        try
        {
            // Singola query WMI che filtra già per ParentProcessId - MOLTO più veloce
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentProcessId}");

            foreach (var o in searcher.Get())
            {
                var obj = (ManagementObject)o;
                try
                {
                    var childProcessId = Convert.ToInt32(obj["ProcessId"]);
                    var childProcess = Process.GetProcessById(childProcessId);
                    childProcesses.Add(childProcess);
                }
                catch (ArgumentException)
                {
                    // Il processo è già terminato
                }
                catch (Exception)
                {
                    // Ignora altri errori
                }
            }
        }
        catch (Exception)
        {
            // Ignora errori durante la ricerca dei processi figli
        }

        return childProcesses;
    }

    /// <summary>
    /// Termina tutti i processi chromedriver.exe e l'albero dei loro processi figli
    /// </summary>
    private static async Task KillChromeDriverProcessesAsync(IWriter? logger)
    {
        try
        {
            var chromeDriverProcesses = Process.GetProcessesByName("chromedriver");

            if (chromeDriverProcesses.Length == 0)
            {
                logger?.WriteLog("Nessun processo ChromeDriver trovato.", LogEntryType.Information);
                return;
            }

            logger?.WriteLog($"Trovati {chromeDriverProcesses.Length} processi ChromeDriver attivi. Terminazione in corso...",
                LogEntryType.Warning);

            foreach (var process in chromeDriverProcesses)
            {
                try
                {
                    var processId = process.Id;
                    var processName = process.ProcessName;

                    // Termina l'intero albero dei processi (ChromeDriver + Chrome)
                    await KillProcessTreeAsync(processId, logger);

                    logger?.WriteLog($"Processo {processName} (PID: {processId}) e relativi figli terminati.",
                        LogEntryType.Information);
                }
                catch (Exception ex)
                {
                    logger?.WriteLog($"Errore durante la terminazione del processo ChromeDriver: {ex.Message}",
                        LogEntryType.Error);
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Attendi un momento per assicurarsi che i processi siano completamente terminati
            await Task.Delay(2000);

            logger?.WriteLog("Tutti i processi ChromeDriver sono stati terminati.", LogEntryType.Information);
        }
        catch (Exception ex)
        {
            logger?.WriteLog($"Errore durante la ricerca/terminazione dei processi ChromeDriver: {ex.Message}",
                LogEntryType.Error);
        }
    }

    /// <summary>
    /// Termina un processo e tutti i suoi processi figli (albero completo)
    /// </summary>
    private static async Task KillProcessTreeAsync(int processId, IWriter? logger)
    {
        try
        {
            // Prima termina tutti i processi figli
            var childProcesses = GetChildProcesses(processId);
            foreach (var childProcess in childProcesses)
            {
                try
                {
                    await KillProcessTreeAsync(childProcess.Id, logger);
                    childProcess.Dispose();
                }
                catch (Exception ex)
                {
                    logger?.WriteLog($"Errore durante la terminazione del processo figlio PID {childProcess.Id}: {ex.Message}",
                        LogEntryType.Warning);
                }
            }

            // Poi termina il processo principale
            try
            {
                var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);

                    // Attendi fino a 5 secondi per la terminazione
                    await Task.Run(() => process.WaitForExit(5000));
                }
                process.Dispose();
            }
            catch (ArgumentException)
            {
                // Il processo è già terminato
            }
            catch (InvalidOperationException)
            {
                // Il processo è già terminato o non esiste più
            }
        }
        catch (Exception ex)
        {
            logger?.WriteLog($"Errore durante la terminazione dell'albero del processo PID {processId}: {ex.Message}",
                LogEntryType.Warning);
        }
    }

    /// <summary>
    /// Rimuove l'attributo ReadOnly da tutti i file e cartelle ricorsivamente
    /// </summary>
    private static void RemoveReadOnlyAttribute(DirectoryInfo directory)
    {
        try
        {
            // Rimuovi readonly dalla directory stessa
            if ((directory.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                directory.Attributes &= ~FileAttributes.ReadOnly;
            }

            // Rimuovi readonly da tutti i file
            foreach (var file in directory.GetFiles())
            {
                try
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }
                }
                catch
                {
                    // Ignora errori sui singoli file
                }
            }

            // Ricorsione sulle sottodirectory
            foreach (var subDir in directory.GetDirectories())
            {
                RemoveReadOnlyAttribute(subDir);
            }
        }
        catch
        {
            // Ignora errori durante la rimozione attributi
        }
    }

    #endregion Private Methods
}
