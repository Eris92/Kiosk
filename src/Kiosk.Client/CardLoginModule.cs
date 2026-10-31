namespace Kiosk.Client;

/// <summary>
/// Owns the one Windows card sign-in provider used by both station and self-service.
/// A prepared provider can wait for the next card; once taken, its caller owns the
/// resulting user session. No kiosk mode starts a second provider over this one.
/// </summary>
internal sealed class CardLoginModule : IDisposable
{
    private Task<FidoSignIn?>? starting;
    private FidoSignIn? ready;
    private bool disposed;

    internal bool IsPreparing => starting is { IsCompleted: false };
    internal bool HasPrepared => ready != null;
    internal bool HasPending => starting != null;

    internal static string FailureMessage(string message)
    {
        if (message.Contains("Anulowano", StringComparison.OrdinalIgnoreCase))
            return "Logowanie anulowane. Wyjmij klucz i włóż go ponownie, aby spróbować jeszcze raz.";
        if (message.Contains("PIN", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("błęd", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("nieprawid", StringComparison.OrdinalIgnoreCase)))
            return "Błędny PIN. Spróbuj ponownie.";
        return "Nie udało się zalogować kartą: " + message;
    }

    internal void Prepare(IntPtr place, int dpi)
    {
        if (disposed || starting != null || ready != null) return;
        try { starting = FidoSignIn.PrepareAsync(place, dpi); }
        catch (Exception ex) { starting = Task.FromException<FidoSignIn?>(ex); }
    }

    /// <summary>Collect a completed preparation without blocking the UI thread.</summary>
    internal bool TryCollect(out string failure)
    {
        failure = "";
        if (starting is not { IsCompleted: true } task) return false;
        starting = null;
        if (task.Status == TaskStatus.RanToCompletion && task.Result is { } provider)
        {
            if (disposed) provider.Dispose();
            else ready = provider;
            return true;
        }
        failure = task.Exception?.GetBaseException() is { } fault
            ? fault.GetType().Name + ": " + fault.Message : FidoSignIn.LastError;
        return true;
    }

    /// <summary>Take the prepared provider, waiting only when its launch is in progress.</summary>
    internal async Task<FidoSignIn?> TakeAsync(IntPtr place, int dpi)
    {
        if (ready is { } prepared)
        {
            ready = null;
            return prepared;
        }
        Prepare(place, dpi);
        if (starting is not { } task) return null;
        starting = null;
        try
        {
            var provider = await task;
            if (!disposed) return provider;
            provider?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Audit.Write("card_login_prepare_failed", ex.Message);
            return null;
        }
    }

    internal FidoSignIn? TakeReady()
    {
        var provider = ready;
        ready = null;
        return provider;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ready?.Dispose();
        ready = null;
        if (starting is { } task)
            _ = task.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); });
        starting = null;
    }
}
