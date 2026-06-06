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
            var token = flashCts.Token;

            // Render the "armed" state synchronously so the caller can be sure the visible
            // state has changed before returning. Anything that needs to react to key
            // presses (like KeyDown wanting the gate to enter the Holding render path)
            // doesn't have to wait on the post-flash Task.Delay.
            bool renderFailed = false;
            try
            {
                await renderFlashAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                renderFailed = true;
            }

            if (renderFailed || _disposed)
            {
                if (ReferenceEquals(_activeFlashCts, flashCts))
                {
                    _activeFlashCts = null;
                }
                flashCts.Dispose();
                return;
            }

            // Fire-and-forget the wait + revert. This is the key change: the caller's lock
            // is released as soon as the armed state is on screen, not after the full
            // animation completes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(duration, token);
                    if (!_disposed)
                    {
                        await renderNormalAsync(token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch
                {
                    // Swallow background-render exceptions; the foreground state machine
                    // will recover on the next user interaction.
                }
                finally
                {
                    if (ReferenceEquals(_activeFlashCts, flashCts))
                    {
                        _activeFlashCts = null;
                    }
                    flashCts.Dispose();
                }
            });
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
