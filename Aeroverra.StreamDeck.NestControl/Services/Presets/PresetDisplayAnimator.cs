namespace Aeroverra.StreamDeck.NestControl.Services.Presets
{
    internal sealed class PresetDisplayAnimator : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdownCts = new();
        private CancellationTokenSource? _activeFlashCts;
        private bool _disposed;

        internal CancellationToken ShutdownToken => _shutdownCts.Token;

        internal async Task RunFlashAsync(
            TimeSpan duration,
            Func<CancellationToken, Task> renderFlashAsync,
            Func<CancellationToken, Task> renderNormalAsync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await CancelActiveFlashAsync();

            var flashCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            _activeFlashCts = flashCts;

            try
            {
                await renderFlashAsync(flashCts.Token);
                await Task.Delay(duration, flashCts.Token);
                if (!_disposed)
                {
                    await renderNormalAsync(flashCts.Token);
                }
            }
            catch (OperationCanceledException) when (flashCts.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_activeFlashCts, flashCts))
                {
                    _activeFlashCts = null;
                }

                flashCts.Dispose();
            }
        }

        internal async Task CancelActiveFlashAsync()
        {
            var flashCts = Interlocked.Exchange(ref _activeFlashCts, null);
            if (flashCts is null)
            {
                return;
            }

            try
            {
                await flashCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            flashCts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await CancelActiveFlashAsync();
            await _shutdownCts.CancelAsync();
            _shutdownCts.Dispose();
        }
    }
}
