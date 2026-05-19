info: https://learn.microsoft.com/it-it/windows-server/administration/windows-commands/sc-create

Per installare il servizio eseguire

sc create SisterBotService binpath="C:\Program Files\SisterBotService\SisterBotService.exe" DisplayName= "Motore di interrogazione di Sister" start= auto depend="MSSQL$SQL2019"

Per eliminare il servizio eseguire
sc delete SisterBotService
sc create SisterBotService binPath= "C:\Percorso\Al\Tuo\Exe.exe" depend= "MSSQLSERVER"


sc create CiroServices binpath="C:\Program Files\CiroServices\CiroServices.exe" DisplayName= "C.i.r.o. Services" start=auto