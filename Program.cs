using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using SisterBot.CRUD.Repository.Interfaces;
using SisterBot.CRUD.Repository.Sql;
using SisterBotService;
using SisterBotService.Classi;


var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();



Common.Configuration = configuration;

if (configuration.GetConnectionString("SisterDatabase") != null) Common.ConnectionData = new ConnectionData(configuration.GetConnectionString("SisterDatabase")!);
Common.DefaultCommandTimeout = configuration.GetConnectionString("DefaultCommandTimeout") == null ? 10000 : Convert.ToInt32(configuration.GetConnectionString("DefaultCommandTimeout"));
Common.TimeZoneAdd = configuration.GetConnectionString("TimeZoneAdd") == null ? 0 : Convert.ToInt32(configuration.GetConnectionString("TimeZoneAdd"));
Common.TenantId = (configuration.GetConnectionString("TenantId") == null ? "" : configuration.GetConnectionString("TenantId")) ?? string.Empty;
Common.ClientId = (configuration.GetConnectionString("ClientId") == null ? "" : configuration.GetConnectionString("ClientId")) ?? string.Empty;
Common.ClientSecret = (configuration.GetConnectionString("ClientSecret") == null ? "" : configuration.GetConnectionString("ClientSecret")) ?? string.Empty;
Common.FromEmail = (configuration.GetConnectionString("FromEmail") == null ? "" : configuration.GetConnectionString("FromEmail")) ?? string.Empty;

// new section in appsettings.json for email notifier configuration
var emailNotifierSection = configuration.GetSection("EmailNotifierExpiringRequest");

if (emailNotifierSection.Exists()) 
    Common.NotifierEmailData = emailNotifierSection.Get<NotifierEmailData>() ?? new NotifierEmailData();

    









Generale.SetUsersAccess();
Generale.LogWriter = new Log.Windows.Writer("SisterBot", "SisterBotLog");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SisterBotService";
});

LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);

builder.Services.AddSingleton<ISISTERBOTRepository, SqlSISTERBOTRepository>(_ => new SqlSISTERBOTRepository(Common.ConnectionData.ConnectionString, Common.DefaultCommandTimeout));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
