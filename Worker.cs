using SisterBot.CRUD.Models;
using SisterBot.CRUD.Repository.Abstract;
using SisterBot.CRUD.Repository.Interfaces;
using SisterBotCore;

namespace SisterBotService
{
    public class Worker(ILogger<Worker> logger, ISISTERBOTRepository repository) : BackgroundService
    {

        #region Private Fields

        private const int StartPort = 9222;

        private readonly List<Core> _coreCommands = [];
        private readonly Lock _coreCommandsLock = new();
        private readonly Lock _portsLock = new();
        private readonly HashSet<int> _remoteDebuggingPorts = [];
        private Core? _core;
        private string _newPagesPath = string.Empty;
        private string _tmpVisure = string.Empty;
        private UserPoolManager? _userPoolManager;

        #endregion Private Fields

        #region Private Enums

        private enum StatoRichiesta : byte
        {
            DaFare = 0,
            InCorso = 1,
            Fatto = 2,
            Errore = 3
        }

        #endregion Private Enums

        #region Public Methods

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            List<Core> coresToStop;
            lock (_coreCommandsLock)
            {
                coresToStop = new List<Core>(_coreCommands);
            }

            foreach (var core in coresToStop)
            {
                core.InterrompiRicerche();
                try
                {
                    await Task.Delay(2000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation during shutdown
                }
            }

            _core?.Dispose();
            _userPoolManager?.Dispose();
            logger.LogInformation("SisterBotService arresto alle: {time}", DateTimeOffset.Now);
            await base.StopAsync(cancellationToken);
        }

        #endregion Public Methods

        #region Protected Methods

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("SisterBotService avvio alle: {time}", DateTimeOffset.Now);

            // Esegue la pulizia iniziale: termina ChromeDriver attivi e pulisce cartelle temp
            await StartupCleanup.ExecuteCleanupAsync(Classi.Generale.LogWriter);

            var executionFolder = AppContext.BaseDirectory;

            var newFolderPath = Path.Combine(executionFolder, "Interrogazioni");
            _newPagesPath = Path.Combine(executionFolder, "NewPages");
            _tmpVisure = Path.Combine(executionFolder, "visure");

            if (!Directory.Exists(newFolderPath)) Directory.CreateDirectory(newFolderPath);
            if (!Directory.Exists(_newPagesPath)) Directory.CreateDirectory(_newPagesPath);
            if (!Directory.Exists(_tmpVisure)) Directory.CreateDirectory(_tmpVisure);

            /*
            await repository.Commands.ExecuteCommandAsync("""
                                                          UPDATE dbo.DATI_RICHIESTE SET STATO = 0, NTENTATIVO=0
                                                          WHERE STATO = 1 AND ID_RICHIESTA IN (
                                                            SELECT ID_RICHIESTA
                                                            FROM dbo.RICHIESTE
                                                            WHERE STATO = 1 AND
                                                                ID_SERVIZIO IN (0,1,2,3,4) AND
                                                          	    NTENTATIVO > 0);
                                                          UPDATE dbo.RICHIESTE SET STATO = 0, NTENTATIVO=NTENTATIVO-1
                                                          WHERE STATO = 1 AND
                                                            ID_SERVIZIO IN (0,1,2,3,4) AND
                                                            NTENTATIVO > 0
                                                          """);

            // Inizializza il UserPoolManager
            if (Classi.Generale.ElencoUtenti != null)
            {
                _userPoolManager = new UserPoolManager(Classi.Generale.ElencoUtenti);
                logger.LogInformation("UserPoolManager inizializzato con {count} utenti",
                    _userPoolManager.GetTotalUsersCount());
            }
            else
            {
                logger.LogError("Impossibile inizializzare UserPoolManager: ElencoUtenti è null");
            }

            await Task.Delay(5000, stoppingToken);
            */

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(5000, stoppingToken);


                continue;

                if (!logger.IsEnabled(LogLevel.Information) || _userPoolManager == null)
                    continue;

                // Verifica se ci sono utenti disponibili
                int usersInUse = await _userPoolManager.GetUsersInUseCountAsync();
                int totalUsers = _userPoolManager.GetTotalUsersCount();

                if (usersInUse >= totalUsers)
                {
                    // Tutti gli utenti sono occupati, attendi
                    logger.LogDebug("Tutti gli utenti occupati ({usersInUse}/{totalUsers}), attesa...",
                        usersInUse, totalUsers);
                    continue;
                }

                var resPrimaRicerca = await repository.Commands.GetDataAsync<decimal>(SqlResource.getPrimaRicercaDaFare);

                if (resPrimaRicerca == 0) continue;

                var readItem = await repository.RICHIESTE.GetItemAsync(resPrimaRicerca);

                if (readItem?.Item == null) continue;

                readItem.Item.STATO = (byte)StatoRichiesta.InCorso;
                readItem.Item.INIZIO = DateTime.Now;
                readItem.Item.FINE = null;
                readItem.Item.NTENTATIVO += 1;

                if (!string.IsNullOrEmpty((await repository.RICHIESTE.UpsertAsync(readItem.Item))?.Exception)) return;

                _ = ExecuteSearchAsync(readItem);

            }
        }

        #endregion Protected Methods

        #region Private Methods

        private async Task ExecuteSearchAsync(ReadItemResult<RICHIESTE> readItem)
        {

            if (readItem.Item == null) return;

            logger.LogInformation("Inizio Ricerca per Id_Richiesta: {idRichiesta} alle {data}",
                readItem.Item.ID_RICHIESTA, DateTimeOffset.Now);

            // CRITICAL: Acquisisce un utente dal pool PRIMA di procedere
            UserAccess? assignedUser;
            if (_userPoolManager != null)
            {
                assignedUser = await _userPoolManager.AcquireUserAsync(TimeSpan.FromMinutes(5));
                if (assignedUser == null)
                {
                    logger.LogWarning("Nessun utente disponibile per Id_Richiesta: {idRichiesta}. Richiesta rimandata.",
                        readItem.Item.ID_RICHIESTA);

                    // Reimposta lo stato a "DaFare" per riprovare dopo
                    readItem.Item.STATO = (byte)StatoRichiesta.DaFare;
                    readItem.Item.NTENTATIVO -= 1; // Decrementa perché verrà riprovata
                    await repository.RICHIESTE.UpsertAsync(readItem.Item);
                    return;
                }

                logger.LogInformation("Utente {username} assegnato a Id_Richiesta: {idRichiesta}",
                    assignedUser.UserName, readItem.Item.ID_RICHIESTA);
            }
            else
            {
                logger.LogError("UserPoolManager non inizializzato per Id_Richiesta: {idRichiesta}",
                    readItem.Item.ID_RICHIESTA);
                readItem.Item.STATO = (byte)StatoRichiesta.Errore;
                await repository.RICHIESTE.UpsertAsync(readItem.Item);
                return;
            }

            int remoteDebuggingPort;
            lock (_portsLock)
            {
                remoteDebuggingPort = StartPort;
                if (_remoteDebuggingPorts.Any())
                    remoteDebuggingPort = _remoteDebuggingPorts.Max() + 1;

                _remoteDebuggingPorts.Add(remoteDebuggingPort);
            }

            // Passa l'utente assegnato al Core
            _core = new Core(remoteDebuggingPort, assignedUser);

            lock (_coreCommandsLock)
            {
                _coreCommands.Add(_core);
            }

            if (await _core.EseguiRicerca((BotCommand.TipoRicerca)readItem.Item.ID_SERVIZIO,
                    readItem.Item.ID_RICHIESTA, _tmpVisure))
            {
                readItem.Item.STATO = (byte)StatoRichiesta.Fatto;
            }
            else
            {
                readItem.Item.STATO = (byte)StatoRichiesta.Errore;
            }

            readItem.Item.FINE = DateTime.Now;
            await repository.RICHIESTE.UpsertAsync(readItem.Item);

            _core.Dispose();

            lock (_coreCommandsLock)
            {
                _coreCommands.Remove(_core);
            }

            lock (_portsLock)
            {
                _remoteDebuggingPorts.Remove(remoteDebuggingPort);
            }

            // CRITICAL: Rilascia l'utente nel pool
            if (assignedUser != null && _userPoolManager != null)
            {
                await _userPoolManager.ReleaseUserAsync(assignedUser);
                logger.LogInformation("Utente {username} rilasciato da Id_Richiesta: {idRichiesta}",
                    assignedUser.UserName, readItem.Item.ID_RICHIESTA);
            }

            logger.LogInformation("Fine Ricerca per Id_Richiesta: {idRichiesta} alle {data}",
                readItem.Item.ID_RICHIESTA, DateTimeOffset.Now);

            try
            {
                var mailSender = new MailSender(Common.TenantId, Common.ClientId, Common.ClientSecret);
                await mailSender.SendMail(Common.FromEmail, readItem.Item.EMAIL?.Trim(), $@"Esito Ricerca per {readItem.Item.DESCRIZIONE}",
                    readItem.Item.STATO == (byte)StatoRichiesta.Fatto
                        ? "La ricerca è stata completata con successo."
                        : "Si è verificato un errore durante la ricerca.");

                logger.LogInformation("Mail inviata a {destinatario} per Id_Richiesta: {idRichiesta} alle {data}",
                    readItem.Item.EMAIL?.Trim(), readItem.Item.ID_RICHIESTA, DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                logger.LogError("Errore nell'invio della mail: {messaggio} per Id_Richiesta: {idRichiesta} alle {data}",
                    ex.Message, readItem.Item.ID_RICHIESTA, DateTimeOffset.Now);
            }
        }

        #endregion Private Methods

    }
}
