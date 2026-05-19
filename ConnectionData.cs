using Microsoft.Data.SqlClient;

namespace SisterBotService;

public class ConnectionData
{
    #region Private Fields

    private string _connectionString = String.Empty; 

    #endregion Private Fields

    #region Public Constructors

    public ConnectionData()
    {
    }

    public ConnectionData(string connectionString)
    {
        ConnectionString = connectionString;
    }

    #endregion Public Constructors

    #region Public Properties

    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            _connectionString = value;
            try
            {
                var sqlbuilder = new SqlConnectionStringBuilder(value);
                if (string.IsNullOrEmpty(ConnectionString)) return;
                InitialCatalog = sqlbuilder.InitialCatalog;
                DataSource = sqlbuilder.DataSource;
            }
            catch (Exception)
            {
                InitialCatalog = string.Empty;
                DataSource = string.Empty;
            }
        }
    }

    public string DataSource { get; private set; } = string.Empty;
    public string InitialCatalog { get; private set; } = string.Empty;

    #endregion Public Properties
}