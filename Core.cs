using System.Data;
using IDatabase;
using Log.Interface;
using SisterBot.CRUD.Models;
using SisterBot.CRUD.Repository.Sql;
using SisterBotCore;
using SisterBotCore.Entities;
using SisterBotService.Classi;
using static SisterBotCore.BotCommand;

namespace SisterBotService;

internal sealed class Core : IDisposable
{

    #region Private Fields

    private const int HoursToLoginInAttempt = 12;
    private const int MaxRetryAttempts = 3;

    private readonly List<DATI_RICHIESTE> _codiciDaRicercare = [];

    private readonly SqlSISTERBOTRepository _repository =
        new(Common.ConnectionData.ConnectionString, Common.DefaultCommandTimeout);

    private readonly BotCommand? _sisterBot;
    private readonly UserAccess? _assignedUser;

    private bool _disposedValue;
    private bool _requestFermaRicerca;

    #endregion Private Fields

    #region Public Constructors

    public Core(int remoteDebuggingPort, UserAccess? assignedUser = null)
    {
        var executionFolder = AppContext.BaseDirectory;
        var newPagesPath = Path.Combine(executionFolder, "NewPages");

        _assignedUser = assignedUser;
        SisterBot = new BotCommand(remoteDebuggingPort, 30000, "", newPagesPath, false, Generale.LogWriter);
        SisterBotStateChanged(new object(), EventArgs.Empty);
    }

    #endregion Public Constructors

    #region Private Destructors

    ~Core()
    {
        Dispose();
    }

    #endregion Private Destructors

    #region Private Enums

    private enum ComandoRicerca : byte
    {
        Nessuno = 0,
        Nuova = 1,
        Riprendi = 2,
        Riprova = 3
    }

    private enum StatoRigaElaborazione : byte
    {
        DaFare = 0,
        InCorso = 1,
        Fatto = 2,
        Errore = 3
    }

    #endregion Private Enums

    #region Private Properties

    private bool IsSearching { get; set; }

    private BotCommand? SisterBot
    {
        get => _sisterBot;

        init
        {
            if (_sisterBot != null) _sisterBot.StateChanged -= SisterBotStateChanged;

            _sisterBot = value;
            if (_sisterBot == null) return;
            _sisterBot.StateChanged += SisterBotStateChanged;
        }
    }

    #endregion Private Properties

    #region Public Methods

    public void Dispose()
    {
        // Non modificare questo codice. Inserire il codice di pulizia nel metodo 'Dispose(bool disposing)'
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    public async Task<bool> EseguiRicerca(TipoRicerca tipoRicerca, decimal idRichiesta, string tmpDirVisure)
    {
        if (SisterBot == null) return false;

        _codiciDaRicercare.Clear();

        if (!await Generale.VerificaConnessioneInternetAsync()) SisterBot?.InterrompiRicerca();

        var resCodiciDaRicercare = await _repository.DATI_RICHIESTE.GetAsync(
            "ID_RICHIESTA = @ID_RICHIESTA",
            [new SerializedDbParameter("@ID_RICHIESTA", SerializedDbParameter.SldDbType.Decimal) { Value = idRichiesta }]);

        if (!string.IsNullOrEmpty(resCodiciDaRicercare?.Exception)) return false;

        if (resCodiciDaRicercare is { Items.Count: 0 }) return true;

        if (resCodiciDaRicercare?.Items != null) _codiciDaRicercare.AddRange(resCodiciDaRicercare.Items);

        await VerificaCodici();

        var comando = ComandoRicerca.Nessuno;

        if (await _repository.Commands.GetDataAsync<bool>(SqlResource.getRicercaIncompleta,
                [new SerializedDbParameter("@ID_RICHIESTA", SerializedDbParameter.SldDbType.Decimal) { Value = idRichiesta }]))
        {
            comando = ComandoRicerca.Riprendi;
            await ResetRiprendi();
        }

        if (comando == ComandoRicerca.Nessuno && await _repository.Commands.GetDataAsync<bool>(
                SqlResource.getErroriInRicerca,
                [new SerializedDbParameter("@ID_RICHIESTA", SerializedDbParameter.SldDbType.Decimal) { Value = idRichiesta }]))
        {
            comando = ComandoRicerca.Riprova;
            await ResetDaRiprovare();
        }

        if (comando == ComandoRicerca.Nessuno)
        {
            comando = ComandoRicerca.Nuova;
            await ResetDaElaborare();
        }

        if (!await LogInProcess()) return false;

        if (GetRigheElaborare(comando).Count != 0) await IniziaRicerca(tipoRicerca, comando, tmpDirVisure);

        return await SisterBot?.LogOut()!;
    }

    #endregion Public Methods

    #region Private Methods

    private void CompleteSearchProcess()
    {
        IsSearching = false;
        _requestFermaRicerca = false;
    }

    private async Task DeleteDataRecordsAsync(DATI_RICHIESTE row)
    {
        await _repository.Commands.ExecuteCommandAsync(
            """
            DELETE FROM dbo.DATI_SOGGETTI WHERE ID_DATI_RICHIESTE = @ID_DATI_RICHIESTE
            DELETE FROM dbo.DATI_NO_RISPOSTA WHERE ID_DATI_RICHIESTE = @ID_DATI_RICHIESTE
            """,
            [new SerializedDbParameter("@ID_DATI_RICHIESTE", SerializedDbParameter.SldDbType.Decimal) { Value = row.ID_DATI_RICHIESTE }]);
    }

    private void Dispose(bool disposing)
    {
        if (_disposedValue) return;
        if (disposing) SisterBot?.Dispose();

        // TODO: liberare risorse non gestite (oggetti non gestiti) ed eseguire l'override del finalizzatore
        // TODO: impostare campi di grandi dimensioni su Null
        _disposedValue = true;
    }

    private List<DATI_RICHIESTE> GetRigheElaborare(ComandoRicerca comando)
    {
        return comando switch
        {
            ComandoRicerca.Riprendi =>
                [.. _codiciDaRicercare.Where(s => s is { STATO: (byte)StatoRigaElaborazione.DaFare})],
            ComandoRicerca.Riprova =>
                [.. _codiciDaRicercare.Where(s => s is { STATO: (byte)StatoRigaElaborazione.Errore})],
            ComandoRicerca.Nuova => [.. _codiciDaRicercare],
            _ => [.. _codiciDaRicercare]
        };
    }

    private async Task<int> HandleRicercaResults(TipoRicerca tipoRicerca, DATI_RICHIESTE row, string tmpDirVisure)
    {
        if (SisterBot == null) return 1;

        if (!row.VERIFICATO)
        {
            await NoRisultato(row.ID_DATI_RICHIESTE, row.CF_PIVA);
            return 1;
        }

        if (Directory.Exists(tmpDirVisure))
        {
            // Ottieni tutti i file nella directory
            var files = Directory.GetFiles(tmpDirVisure);

            // Elimina ogni file
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }

        var result = await SisterBot.RicercaSister(tipoRicerca, row.CONSERVATORIA, row.CF_PIVA.Trim(), row.ID_ORDINE,
            row.ALTRO1, row.ALTRO1, tmpDirVisure);

        if (result == 0) return 0;
        await SaveDataResult(tipoRicerca, row, tmpDirVisure);

        return result;
    }

    private async Task<bool> HandleSearchRequest()
    {
        if (SisterBot == null) return false;

        var starCheckAttempt = DateTime.Now;
        do
        {
            if (SisterBot.BotStateInfo != BotState.NoConnection) break;
            await Task.Delay(60000);
        } while ((DateTime.Now - starCheckAttempt).Hours < HoursToLoginInAttempt && !_requestFermaRicerca);
        
        if (!_requestFermaRicerca && SisterBot.BotStateInfo == BotState.LoggedIn) return true;

        var reqFermaRicerca = _requestFermaRicerca;

        InterrompiRicerche();

        if (reqFermaRicerca)
        {
            _requestFermaRicerca = false;
            return false;
        }

        _requestFermaRicerca = reqFermaRicerca;
        
        if (await LogInProcess()) return true;
        _requestFermaRicerca = false;
        return false;

    }

    private async Task IniziaRicerca(TipoRicerca tipoRicerca, ComandoRicerca comando, string tmpDirVisure)
    {
        if (_codiciDaRicercare.Count <= 0) return;

        if (SisterBot is not { BotStateInfo: BotState.LoggedIn } ||
            SisterBot.BotStateInfo == BotState.SessionExpired) return;

        IsSearching = true;

        var righeDaElaborareRows = GetRigheElaborare(comando);

        foreach (var row in righeDaElaborareRows)
        {
            var attemptCount = 0;
            var operationSucceeded = false;

            while (attemptCount < MaxRetryAttempts && !operationSucceeded && !_requestFermaRicerca)
            {
                attemptCount++;

                try
                {
                    await DeleteDataRecordsAsync(row);

                    if (!await HandleSearchRequest())
                    {
                        CompleteSearchProcess();
                        return;
                    }

                    await UpdateRowStatusToQuery(row);

                    var result = await HandleRicercaResults(tipoRicerca, row, tmpDirVisure);

                    if (result == 0)
                    {
                        Generale.LogWriter?.WriteLog(
                            $"Tentativo {attemptCount}/{MaxRetryAttempts} fallito per ID_DATI_RICHIESTE={row.ID_DATI_RICHIESTE}. Nessun risultato.",
                            LogEntryType.Warning);

                        if (attemptCount < MaxRetryAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attemptCount))); // Exponential backoff
                            continue;
                        }
                    }

                    await UpdateRowStatusToResult(row, result);
                    operationSucceeded = true;

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    Generale.LogWriter?.WriteLog(
                        $"IniziaRicerca Tentativo {attemptCount}/{MaxRetryAttempts} - Message: {ex.Message}\r\nStackTrace: {ex.StackTrace}",
                        LogEntryType.Error);

                    if (attemptCount < MaxRetryAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attemptCount))); // Exponential backoff
                    }
                    else
                    {
                        // After max retries, mark as error
                        row.STATO = (byte)StatoRigaElaborazione.Errore;
                        row.FINE = DateTime.Now;
                        await _repository.DATI_RICHIESTE.UpsertAsync(row);
                    }
                }
            }
        }

        CompleteSearchProcess();
    }

    public void InterrompiRicerche()
    {
        if (IsSearching) _requestFermaRicerca = true;
        SisterBot?.InterrompiRicerca();
    }

    private async Task<bool> LogInProcess()
    {
        if (SisterBot == null) return false;

        // Se è stato assegnato un utente specifico, usa solo quello
        var usersToTry = _assignedUser != null
            ? new RootAccess { Users = new List<UserAccess> { _assignedUser } }
            : Generale.ElencoUtenti;

        if (usersToTry == null)
        {
            Generale.LogWriter?.WriteLog("Nessun utente disponibile per il login", LogEntryType.Error);
            return false;
        }

        var starLoginAttempt = DateTime.Now;
        var logInResult = false;
        do
        {
            if (await Generale.VerificaConnessioneInternetAsync())
            {
                logInResult = await SisterBot.LogIn(usersToTry);
                if (logInResult)
                {
                    if (_assignedUser != null)
                    {
                        Generale.LogWriter?.WriteLog(
                            $"Login eseguito con utente assegnato: {_assignedUser.UserName}",
                            LogEntryType.Information);
                    }
                    break;
                }
                Generale.LogWriter?.WriteLog("LogInProcess fallito, nuovo tentativo di accesso tra 60 secondi",
                    LogEntryType.Warning);

            }
            else
            {
                Generale.LogWriter?.WriteLog("Nessuna connessione internet, nuovo tentativo di accesso tra 60 secondi",
                    LogEntryType.Warning);
            }
            await Task.Delay(60000);
        } while ((DateTime.Now - starLoginAttempt).Hours < HoursToLoginInAttempt && !_requestFermaRicerca);

        Generale.LogWriter?.WriteLog($"Il processo di login è {(logInResult ? "stato eseguito" : "fallito")}", logInResult ? LogEntryType.Information : LogEntryType.Warning);
        return logInResult;
    }
    
    private async Task NoRisultato(decimal idDatiRichieste, string codFiscale)
    {
        await _repository.DATI_NO_RISPOSTA.UpsertAsync(new DATI_NO_RISPOSTA
        {
            ID_DATI_RICHIESTE = idDatiRichieste,
            COD_FISCALE = codFiscale
        });
    }

    private async Task ResetDaElaborare()
    {
        foreach (var row in _codiciDaRicercare)
        {
            row.STATO = (byte)StatoRigaElaborazione.DaFare;
            row.NTENTATIVO = 0;
            row.INIZIO = null;
            row.FINE = null;

            await _repository.DATI_RICHIESTE.UpsertAsync(row);
        }
    }

    private async Task ResetDaRiprovare()
    {
        foreach (var row in _codiciDaRicercare
                     .Where(s => s.STATO == (byte)StatoRigaElaborazione.Errore))
        {
            row.INIZIO = null;
            row.FINE = null;

            await _repository.DATI_RICHIESTE.UpsertAsync(row);
        }
    }

    private async Task ResetRiprendi()
    {
        foreach (var row in _codiciDaRicercare
                     .Where(s => s.STATO != (byte)StatoRigaElaborazione.Fatto))
        {
            row.STATO = (byte)StatoRigaElaborazione.DaFare;
            row.INIZIO = null;
            row.FINE = null;

            await _repository.DATI_RICHIESTE.UpsertAsync(row);
        }
    }

    private async Task SalvaDatiPf(decimal idRichiesta, RisultatiCatasto risultati)
    {
        foreach (var datiRispostaPf in risultati.RispostaPf)
        {
            var resSoggetto = await _repository.DATI_SOGGETTI.UpsertAsync(new DATI_SOGGETTI
            {
                ID = datiRispostaPf.Id,
                ID_DATI_RICHIESTE = idRichiesta,
                COD_FISCALE = datiRispostaPf.CodFiscale,
                NOME = datiRispostaPf.Nome,
                COGNOME = datiRispostaPf.Cognome,
                SESSO = datiRispostaPf.Sesso,
                DATA_NASCITA = datiRispostaPf.DataNascita,
                LUOGO_NASCITA = datiRispostaPf.LuogoNascita,
                PG = false
            });

            if (resSoggetto.Item == null || !string.IsNullOrEmpty(resSoggetto.Exception) ||
                resSoggetto.Item.ID_DATI_SOGGETTO == 0) continue;
            await SalvaRisultatiProvincia(resSoggetto.Item.ID_DATI_SOGGETTO, datiRispostaPf.IndiceBaseRicerca,
                risultati);
        }
    }

    private async Task SalvaDatiPf(decimal idRichiesta, RisultatiIspezioni risultati)
    {
        foreach (var datiRispostaPf in risultati.RispostaPf)
        {
            var resSoggetto = await _repository.DATI_SOGGETTI.UpsertAsync(new DATI_SOGGETTI
            {
                ID = datiRispostaPf.Id,
                ID_DATI_RICHIESTE = idRichiesta,
                COD_FISCALE = datiRispostaPf.CodFiscale,
                NOME = datiRispostaPf.Nome,
                COGNOME = datiRispostaPf.Cognome,
                SESSO = datiRispostaPf.Sesso,
                DATA_NASCITA = datiRispostaPf.DataNascita,
                LUOGO_NASCITA = datiRispostaPf.LuogoNascita,
                PG = false
            });

            if (resSoggetto.Item == null || !string.IsNullOrEmpty(resSoggetto.Exception) ||
                resSoggetto.Item.ID_DATI_SOGGETTO == 0) continue;

            await SalvaRisultatiProvincia(resSoggetto.Item.ID_DATI_SOGGETTO, datiRispostaPf.IndiceBaseRicerca,
                risultati);
            await SalvaRisultatiDatiAtti(idRichiesta, datiRispostaPf.IndiceBaseRicerca, risultati);
        }
    }

    private async Task SalvaDatiPg(decimal idRichiesta, RisultatiCatasto risultati)
    {
        foreach (var datiRispostaPg in risultati.RispostaPg)
        {
            var resSoggetto = await _repository.DATI_SOGGETTI.UpsertAsync(new DATI_SOGGETTI
            {
                ID = datiRispostaPg.Id,
                ID_DATI_RICHIESTE = idRichiesta,
                COD_FISCALE = datiRispostaPg.CodFiscale,
                DENOMINAZIONE = datiRispostaPg.Denominazione,
                PG = true
            });

            if (resSoggetto.Item == null || !string.IsNullOrEmpty(resSoggetto.Exception) ||
                resSoggetto.Item.ID_DATI_SOGGETTO == 0) continue;

            await SalvaRisultatiProvincia(resSoggetto.Item.ID_DATI_SOGGETTO, datiRispostaPg.IndiceBaseRicerca,
                risultati);
        }
    }

    private async Task SalvaDatiPg(decimal idRichiesta, RisultatiIspezioni risultati)
    {
        foreach (var datiRispostaPg in risultati.RispostaPg)
        {
            var resSoggetto = await _repository.DATI_SOGGETTI.UpsertAsync(new DATI_SOGGETTI
            {
                ID = datiRispostaPg.Id,
                ID_DATI_RICHIESTE = idRichiesta,
                COD_FISCALE = datiRispostaPg.CodFiscale,
                DENOMINAZIONE = datiRispostaPg.Denominazione,
                PG = true
            });

            if (resSoggetto.Item == null || !string.IsNullOrEmpty(resSoggetto.Exception) ||
                resSoggetto.Item.ID_DATI_SOGGETTO == 0) continue;

            await SalvaRisultatiProvincia(resSoggetto.Item.ID_DATI_SOGGETTO, datiRispostaPg.IndiceBaseRicerca,
                risultati);
            await SalvaRisultatiDatiAtti(idRichiesta, datiRispostaPg.IndiceBaseRicerca, risultati);
        }
    }

    private async Task SalvaRisultatiDatiAtti(decimal idRichiesta, int indiceBaseRicerca, RisultatiIspezioni risultati)
    {
        foreach (var datiAtto in risultati.ElencoAttiIspezioni.Where(x => x.IndiceBaseRicerca == indiceBaseRicerca))
            await _repository.DATI_ATTI_ISPEZIONI.UpsertAsync(
                new DATI_ATTI_ISPEZIONI
                {
                    ID_DATI_RICHIESTE = idRichiesta,
                    CONSERVATORIA = datiAtto.Conservatoria,
                    NUMERO = datiAtto.Numero,
                    TIPO_ATTO = datiAtto.TipoAtto,
                    EFFETTO = datiAtto.Effetto,
                    DATA_ATTO = datiAtto.DataAtto,
                    DESCRIZIONE_ATTO = datiAtto.DescrizioneAtto,
                    NUMERO_GENERALE = datiAtto.NumeroGenerale,
                    NUMERO_PARTICOLARE = datiAtto.NumeroParticolare,
                    DATI_REPERTORIO = datiAtto.DatiRepertorio,
                    PUBBLICO_UFFICIALE = datiAtto.PubblicoUfficiale,
                });
    }

    private async Task SalvaRisultatiImmobile(DATI_PROVINCIA datiProvincia, int indiceBaseRicerca,
        RisultatiCatasto risultati)
    {
        foreach (var datiImmobileProvincia in risultati.ImmobiliProvincia.Where(x =>
                     x.IndiceBaseRicerca == indiceBaseRicerca && x.IdProvincia == datiProvincia.ID))
        {
            var resDatiImmobile = await _repository.DATI_IMMOBILI_PROVINCIA.UpsertAsync(new DATI_IMMOBILI_PROVINCIA
            {
                ID_DATI_PROVINCIA = datiProvincia.ID_DATI_PROVINCIA,
                ID_PROVINCIA = datiProvincia.ID,
                ID = datiImmobileProvincia.Id,
                TIPO_CATASTO = datiImmobileProvincia.TipoCatasto,
                TITOLARITA = datiImmobileProvincia.Titolarita,
                UBICAZIONE = datiImmobileProvincia.Ubicazione,
                COMUNE = datiImmobileProvincia.Comune,
                PROVINCIA = datiImmobileProvincia.Provincia,
                INDIRIZZO = datiImmobileProvincia.Indirizzo,
                FOGLIO = datiImmobileProvincia.Foglio,
                PARTICELLA = datiImmobileProvincia.Particella,
                SUBANNO = datiImmobileProvincia.Subanno,
                CLASSAMENTO = datiImmobileProvincia.Classamento,
                CLASSE = datiImmobileProvincia.Classe,
                CONSISTENZA = datiImmobileProvincia.Consistenza,
                RENDITA = datiImmobileProvincia.Rendita,
                PARTITA = datiImmobileProvincia.Partita,
                ALTRI_DATI = datiImmobileProvincia.AltriDati
            });

            if (resDatiImmobile.Item == null || !string.IsNullOrEmpty(resDatiImmobile.Exception) ||
                resDatiImmobile.Item.ID_DATI_IMMOBILE_PROVINCIA == 0) continue;
            await SalvaRisultatiIntestatariImmobile(resDatiImmobile.Item, risultati);
        }
    }

    private async Task SalvaRisultatiImmobile(DATI_PROVINCIA datiProvincia, int indiceBaseRicerca,
        RisultatiIspezioni risultati)
    {
        foreach (var datiImmobileProvincia in risultati.ImmobiliProvincia.Where(x =>
                     x.IndiceBaseRicerca == indiceBaseRicerca && x.IdProvincia == datiProvincia.ID))
            await _repository.DATI_IMMOBILI_PROVINCIA.UpsertAsync(new DATI_IMMOBILI_PROVINCIA
            {
                ID_DATI_PROVINCIA = datiProvincia.ID_DATI_PROVINCIA,
                ID_PROVINCIA = datiProvincia.ID,
                ID = datiImmobileProvincia.Id,
                TIPO_CATASTO = datiImmobileProvincia.TipoCatasto,
                TITOLARITA = datiImmobileProvincia.Titolarita,
                UBICAZIONE = datiImmobileProvincia.Ubicazione,
                COMUNE = datiImmobileProvincia.Comune,
                PROVINCIA = datiImmobileProvincia.Provincia,
                INDIRIZZO = datiImmobileProvincia.Indirizzo,
                FOGLIO = datiImmobileProvincia.Foglio,
                PARTICELLA = datiImmobileProvincia.Particella,
                SUBANNO = datiImmobileProvincia.Subanno,
                CLASSAMENTO = datiImmobileProvincia.Classamento,
                CLASSE = datiImmobileProvincia.Classe,
                CONSISTENZA = datiImmobileProvincia.Consistenza,
                RENDITA = datiImmobileProvincia.Rendita,
                PARTITA = datiImmobileProvincia.Partita,
                ALTRI_DATI = datiImmobileProvincia.AltriDati
            });
    }

    private async Task SalvaRisultatiIntestatariImmobile(DATI_IMMOBILI_PROVINCIA datiImmobile,
        RisultatiCatasto risultati)
    {
        foreach (var datiIntestatarioImmobile in risultati.IntestatariImmobile.Where(x =>
                     x.IdImmobile == datiImmobile.ID))
            await _repository.DATI_INTESTATARI_IMMOBILE.UpsertAsync(
                new DATI_INTESTATARI_IMMOBILE
                {
                    ID_DATI_IMMOBILE_PROVINCIA = datiImmobile.ID_DATI_IMMOBILE_PROVINCIA,
                    IDCF = datiIntestatarioImmobile.IdCf ?? string.Empty,
                    ID_PROVINCIA = datiIntestatarioImmobile.IdProvincia ?? string.Empty,
                    ID_IMMOBILE = datiIntestatarioImmobile.IdImmobile ?? string.Empty,
                    NOMINATIVO = datiIntestatarioImmobile.Nominativo,
                    COD_FISCALE = datiIntestatarioImmobile.CodFiscale,
                    TITOLARITA = datiIntestatarioImmobile.Titolarita,
                    QUOTA = datiIntestatarioImmobile.Quota
                });
    }

    private async Task SalvaRisultatiProvincia(decimal idDatiSoggetto, int indiceBaseRicerca,
        RisultatiCatasto risultati)
    {
        foreach (var datiProvincia in risultati.ElencoProvince.Where(x => x.IndiceBaseRicerca == indiceBaseRicerca))
        {
            var resDatiRispostaPg = await _repository.DATI_PROVINCIA.UpsertAsync(new DATI_PROVINCIA
            {
                ID_DATI_SOGGETTO = idDatiSoggetto,
                IDCF = datiProvincia.IdCf ?? string.Empty,
                ID = datiProvincia.Id,
                PROVINCIA = datiProvincia.Provincia,
                FABBRICATI = datiProvincia.Fabbricati,
                TERRENI = datiProvincia.Terreni,
                VISURA = datiProvincia.Visura,
                DOCUMENTO_DIFFERITO = datiProvincia.Visura
            });

            if (resDatiRispostaPg.Item == null || !string.IsNullOrEmpty(resDatiRispostaPg.Exception) ||
                resDatiRispostaPg.Item.ID_DATI_SOGGETTO == 0) continue;
            await SalvaRisultatiImmobile(resDatiRispostaPg.Item, indiceBaseRicerca, risultati);
        }
    }

    private async Task SalvaRisultatiProvincia(decimal idDatiSoggetto, int indiceBaseRicerca,
        RisultatiIspezioni risultati)
    {
        foreach (var datiProvincia in risultati.ElencoProvince.Where(x => x.IndiceBaseRicerca == indiceBaseRicerca))
        {
            var resDatiRispostaPg = await _repository.DATI_PROVINCIA.UpsertAsync(new DATI_PROVINCIA
            {
                ID_DATI_SOGGETTO = idDatiSoggetto,
                IDCF = datiProvincia.IdCf ?? string.Empty,
                ID = datiProvincia.Id,
                PROVINCIA = datiProvincia.Provincia,
                FABBRICATI = datiProvincia.Fabbricati,
                TERRENI = datiProvincia.Terreni,
                VISURA = datiProvincia.Visura,
                DOCUMENTO_DIFFERITO = datiProvincia.Visura
            });

            if (resDatiRispostaPg.Item == null || !string.IsNullOrEmpty(resDatiRispostaPg.Exception) ||
                resDatiRispostaPg.Item.ID_DATI_SOGGETTO == 0) continue;
            await SalvaRisultatiImmobile(resDatiRispostaPg.Item, indiceBaseRicerca, risultati);
        }
    }

    private async Task SaveDataResult(TipoRicerca tipoRicerca, DATI_RICHIESTE row, string cartellaDestinazione)
    {
        if (tipoRicerca == TipoRicerca.IspezioneIpotecaria)
        {
            switch (row.CF_PIVA.Length)
            {
                case 11 when SisterBot?.RispostaCatasto.RispostaPg.Count > 0:
                    if (SisterBot?.RispostaCatasto.RispostaPg == null) return;
                    await SalvaDatiPg(row.ID_DATI_RICHIESTE, SisterBot?.RispostaIspezioni!);
                    break;
                case 11:
                    await NoRisultato(row.ID_DATI_RICHIESTE, row.CF_PIVA);
                    break;
                case 16 when SisterBot?.RispostaCatasto.RispostaPf.Count > 0:
                    if (SisterBot?.RispostaCatasto.RispostaPf == null) return;
                    await SalvaDatiPf(row.ID_DATI_RICHIESTE, SisterBot?.RispostaIspezioni!);
                    break;
                case 16:
                    await NoRisultato(row.ID_DATI_RICHIESTE, row.CF_PIVA);
                    break;
            }
        }
        else
        {
            switch (row.CF_PIVA.Length)
            {
                case 11 when SisterBot?.RispostaCatasto.RispostaPg.Count > 0:
                    if (SisterBot?.RispostaCatasto.RispostaPg == null) return;
                    await SalvaDatiPg(row.ID_DATI_RICHIESTE, SisterBot?.RispostaCatasto!);
                    break;
                case 11:
                    await NoRisultato(row.ID_DATI_RICHIESTE, row.CF_PIVA);
                    break;
                case 16 when SisterBot?.RispostaCatasto.RispostaPf.Count > 0:
                    if (SisterBot?.RispostaCatasto.RispostaPf == null) return;
                    await SalvaDatiPf(row.ID_DATI_RICHIESTE, SisterBot?.RispostaCatasto!);
                    break;
                case 16:
                    await NoRisultato(row.ID_DATI_RICHIESTE, row.CF_PIVA);
                    break;
            }

            if (!Directory.Exists(cartellaDestinazione) ||
                tipoRicerca is not (TipoRicerca.VisuraCatastaleEasy or TipoRicerca.VisuraCatastaleUfficiale or TipoRicerca.IspezioneCatastaleSemplice))
                return;

            var files = Directory.GetFiles(cartellaDestinazione);

            foreach (var file in files)
            {
                var fileContent = await File.ReadAllBytesAsync(file);
                var fileName = Path.GetFileName(file);

                await _repository.FILE.UpsertAsync(new FILE
                {
                    ID_DATI_RICHIESTE = row.ID_DATI_RICHIESTE,
                    NOME_FILE = fileName,
                    FILE_STREAM = fileContent
                });
            }
        }
    }
    
    private void SisterBotStateChanged(object sender, EventArgs e)
    {
        //if (SisterBot == null) return;
    }

    private async Task UpdateRowStatusToQuery(DATI_RICHIESTE row)
    {
        row.STATO = (byte)StatoRigaElaborazione.InCorso;
        row.INIZIO = DateTime.Now;
        row.NTENTATIVO += 1;
        await _repository.DATI_RICHIESTE.UpsertAsync(row);
    }

    private async Task UpdateRowStatusToResult(DATI_RICHIESTE row, int result)
    {
        row.STATO = result > 0 ? (byte)StatoRigaElaborazione.Fatto : (byte)StatoRigaElaborazione.Errore;
        row.FINE = DateTime.Now;
        row.ID_UTENTE = SisterBot?.CurrentUser?.UserName;
        await _repository.DATI_RICHIESTE.UpsertAsync(row);
    }

    private async Task VerificaCodici()
    {
        foreach (var datiRichieste in _codiciDaRicercare.Where(x => !x.CF_PIVA_VERIFICATA))
        {
            datiRichieste.VERIFICATO = Generale.VerificaCodice(datiRichieste.CF_PIVA);
            datiRichieste.CF_PIVA_VERIFICATA = true;

            await _repository.DATI_RICHIESTE.UpsertAsync(datiRichieste);
        }
    }

    #endregion Private Methods

    // // TODO: eseguire l'override del finalizzatore solo se 'Dispose(bool disposing)' contiene codice per liberare risorse non gestite
    // ~Core()
    // {
    //     // Non modificare questo codice. Inserire il codice di pulizia nel metodo 'Dispose(bool disposing)'
    //     Dispose(disposing: false);
    // }
}