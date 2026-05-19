using SisterBotCore;

namespace SisterBotService;

/// <summary>
/// Gestisce il pool di utenti disponibili garantendo che ogni utente sia usato da una sola interrogazione alla volta
/// </summary>
internal class UserPoolManager(RootAccess rootAccess) : IDisposable
{
    #region Private Fields

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly RootAccess _rootAccess = rootAccess ?? throw new ArgumentNullException(nameof(rootAccess));
    private readonly HashSet<string> _usersInUse = [];
    private bool _disposed;

    #endregion Private Fields

    #region Public Methods

    /// <summary>
    /// Acquisisce un utente disponibile dal pool
    /// </summary>
    /// <param name="timeout">Timeout massimo per attendere un utente disponibile</param>
    /// <param name="cancellationToken">Token di cancellazione</param>
    /// <returns>UserAccess se disponibile, null se timeout o nessun utente disponibile</returns>
    public async Task<UserAccess?> AcquireUserAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_rootAccess.Users == null || _rootAccess.Users.Count == 0)
                    return null;

                // Cerca un utente attivo non in uso
                var availableUser = _rootAccess.Users
                    .FirstOrDefault(u => !_usersInUse.Contains(u.UserName));

                if (availableUser != null)
                {
                    _usersInUse.Add(availableUser.UserName);
                    return availableUser;
                }
            }
            finally
            {
                _lock.Release();
            }

            // Nessun utente disponibile, attendi prima di riprovare
            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock?.Dispose();
    }

    /// <summary>
    /// Restituisce il numero totale di utenti disponibili
    /// </summary>
    public int GetTotalUsersCount()
    {
        return _rootAccess.Users?.Count ?? 0;
    }

    /// <summary>
    /// Restituisce il numero di utenti attualmente in uso
    /// </summary>
    public async Task<int> GetUsersInUseCountAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _usersInUse.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Rilascia un utente precedentemente acquisito
    /// </summary>
    public async Task ReleaseUserAsync(UserAccess? user)
    {
        if (user == null) return;

        await _lock.WaitAsync();
        try
        {
            _usersInUse.Remove(user.UserName);
        }
        finally
        {
            _lock.Release();
        }
    }

    #endregion Public Methods
}
