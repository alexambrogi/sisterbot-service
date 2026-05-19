# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Service Does

SisterBotService is a .NET 10 Windows background service that automates Italian property registry searches (visure catastali) and mortgage inspections (ispezioni ipotecarie) via Selenium WebDriver against the Sister portal. It polls a SQL Server database queue, executes web searches using a pool of user accounts, and writes results back to the database before sending email notifications.

## Build & Run

```powershell
# Build
dotnet build

# Run locally (uses appsettings.Development.json)
dotnet run

# Publish for production deployment
dotnet publish -c Release
```

**Install as Windows Service (requires admin):**
```powershell
sc create SisterBotService binPath="C:\Program Files\SisterBotService\SisterBotService.exe" DisplayName="Motore di interrogazione di Sister" start=auto depend="MSSQL$SQL2019"
sc start SisterBotService
sc stop SisterBotService
sc delete SisterBotService
```

Logs are written to **Windows Event Viewer** under source `SisterBotService`.

## Project References

The solution has multiple projects in sibling directories:

- **SisterBotCore** — Selenium/Chrome automation engine; contains `BotCommand`, and per-search-type engines in `Engines/` (`VisureCatastali.cs`, `IspezioniIpotecarie.cs`)
- **SisterBot.CRUD.Repository** — Repository interfaces (`ISISTERBOTRepository`, table-level repos)
- **SisterBot.CRUD.Repository.Sql** — ADO.NET SQL Server implementation
- **Log.Windows** — Windows Event Log writer abstraction
- **Helper** — Shared extension methods

## Architecture

### Request Lifecycle

The `RICHIESTE` table is the queue. Each row is a search job with `STATO` (0=DaFare, 1=InCorso, 2=Fatto, 3=Errore) and `NTENTATIVO` retry counter (max 3). Each job has child `DATI_RICHIESTE` rows — one per subject/fiscal code to search.

### Worker Main Loop (`Worker.cs` → `Worker.ExecuteAsync`)

1. **Startup**: `StartupCleanup` kills lingering `chromedriver.exe` processes (via WMI) and deletes `C:\Temp\SisterBot*` folders
2. **Initialize**: Load user pool from `UsersAccess.json`
3. **Poll every 5 seconds**:
   - If no users available, skip iteration
   - Query `getPrimaRicercaDaFare` for next pending request (STATO=0 or 3, NTENTATIVO<3)
   - Acquire a user from `UserPoolManager` (5-minute timeout)
   - Fire-and-forget `ExecuteSearchAsync()` — searches run in parallel, one per user
   - Release user back to pool after search; send email notification
4. **Shutdown**: Wait 2s, interrupt all active searches, dispose Chrome instances

### UserPoolManager (`UserPoolManager.cs`)

Thread-safe credential pool using `SemaphoreSlim(1,1)` and a `HashSet<string>` of in-use usernames. `AcquireUserAsync(timeout)` polls until a user is free or times out. Each search holds exactly one user for its entire duration to prevent concurrent logins with the same credentials.

### Search Execution (`Core.cs` → `Core.EseguiRicerca`)

1. Load all `DATI_RICHIESTE` rows for the request
2. Determine mode: `Riprendi` (resume incomplete), `Riprova` (retry errors), or `Nuova` (fresh)
3. Create `BotCommand` with a **unique Chrome remote debugging port** (starting at 9222, tracked in a thread-safe `HashSet<int>`) for browser isolation
4. Login → iterate subjects → execute search → save results → logout
5. Update request STATO

### Chrome Isolation

Each concurrent search launches its own Chrome instance attached to a unique remote debugging port. Ports are allocated from a shared set protected by a `Lock` object (`_portsLock`). Active `BotCommand` instances are tracked in `_coreCommands` (protected by `_coreCommandsLock`) for graceful shutdown.

## Key Configuration

**`appsettings.json`** — Contains SQL Server connection string (server `192.168.28.26\SQL2019`), Azure/Entra credentials for Microsoft Graph email sending (`TenantId`, `ClientId`, `ClientSecret`), and `FromEmail`. The Development override (`appsettings.Development.json`) may override these.

**`UsersAccess.json`** — The user pool. Each entry has `UserName` (Italian fiscal code), `Password`, `IsActive`, and `IsExpired`. Add/disable users here to scale or maintain the pool.

## Database Key Tables

| Table | Role |
|---|---|
| `RICHIESTE` | Job queue master |
| `DATI_RICHIESTE` | Per-subject search items |
| `DATI_SOGGETTI` | Search results (persons/entities found) |
| `DATI_IMMOBILI_*` | Cadastral property details |
| `DATI_NO_RISPOSTA` | No-match records |

## No Tests

There are no automated tests. Validation is done by running the service against the real database and verifying Event Log output.
