using System.Collections.Concurrent;
using NmeaTransport.Clients;

namespace NmeaTransport.Internal;

internal sealed class NmeaMessageHandlerRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, NmeaMessageHandler>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(string header, NmeaMessageHandler handler)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("The header must not be null or whitespace.", nameof(header));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var registrations = _handlers.GetOrAdd(header, _ => new ConcurrentDictionary<Guid, NmeaMessageHandler>());
        var registrationId = Guid.NewGuid();
        registrations[registrationId] = handler;

        return new HandlerRegistration(this, header, registrationId);
    }

    public async Task DispatchAsync(NmeaMessage message, CancellationToken ct, Action<string> log)
    {
        if (!_handlers.TryGetValue(message.Header, out var registrations) || registrations.IsEmpty)
        {
            return;
        }

        foreach (var handler in registrations.Values)
        {
            try
            {
                await handler(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                log($"Handler error for header '{message.Header}': {exception.Message}");
            }
        }
    }

    private void Remove(string header, Guid registrationId)
    {
        if (!_handlers.TryGetValue(header, out var registrations))
        {
            return;
        }

        registrations.TryRemove(registrationId, out _);

        if (registrations.IsEmpty)
        {
            _handlers.TryRemove(header, out _);
        }
    }

    private sealed class HandlerRegistration : IDisposable
    {
        private readonly NmeaMessageHandlerRegistry _owner;
        private readonly string _header;
        private readonly Guid _registrationId;
        private int _disposed;

        public HandlerRegistration(NmeaMessageHandlerRegistry owner, string header, Guid registrationId)
        {
            _owner = owner;
            _header = header;
            _registrationId = registrationId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.Remove(_header, _registrationId);
        }
    }
}
